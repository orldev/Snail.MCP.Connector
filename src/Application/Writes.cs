using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Snail.MCP.Connector.Application.Ports;
using Snail.MCP.Connector.Configuration;

namespace Snail.MCP.Connector.Application;

/// <summary>Writes to a company database — a MongoDB collection or a SQL table: proposed by an agent, decided by a person on the stand, run once by the writing user.</summary>
/// <remarks>
/// The owner's decision of 15.10.2026. A proposal is dry-run by the read-only user — how many documents it matches, a few
/// of them as they are — and kept here, so the person reads what will run and not the agent's account of it. The decision
/// comes only through the dashboard's door, signed with a key no agent ever sees; the write runs only for the task that
/// proposed it, only once, and only within <see cref="ConnectorOptions.ProposalDays"/>. Every step is in the journal.
/// </remarks>
public sealed class Writes(
    ISources sources,
    IDocuments documents,
    ITables tables,
    IProposals proposals,
    IQueryJournal journal,
    SessionCopy copy,
    TimeProvider clock,
    IOptions<ConnectorOptions> options)
{
    /// <summary>Dry-runs the write and keeps it as a proposal for a person to decide.</summary>
    public async Task<Answer<WriteProposal>> ProposeAsync(string source, string collection, string operation, string? filter, string? change, string reason, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.Value.ApprovalKey))
            return Answer.Failed<WriteProposal>(Messages.NoApprovals, Messages.NoApprovalsHint);

        var context = await ReachAsync(source, collection, cancellationToken);
        if (context.Value is not { } reached)
            return Answer.Failed<WriteProposal>(context.Error!, context.Hint);

        if (!reached.Source.Source.CanWrite)
            return Answer.Failed<WriteProposal>(Messages.NoWriter, Messages.NoWriterHint);

        var kind = WriteRules.Operation(operation);
        if (kind.Error is not null)
            return Answer.Failed<WriteProposal>(kind.Error);

        var shaped = reached.Source.Source.Kind is SourceKind.MongoDb
            ? await ShapeAsync(reached, collection, kind.Value, filter, change, cancellationToken)
            : await ShapeTableAsync(reached, collection, kind.Value, filter, change, cancellationToken);
        if (shaped.Value is not { } proposal)
            return shaped;

        var proposed = proposal with { Reason = reason.Trim() };
        if (!await IsRecordedAsync(proposed, "propose", proposed.Matched, null, cancellationToken))
            return Answer.Failed<WriteProposal>(Messages.JournalDown, Messages.JournalDownHint);

        await proposals.SaveAsync(proposed, cancellationToken);

        return Answer.Ok(proposed);
    }

    /// <summary>Runs an allowed proposal of this session's task by the writing user.</summary>
    public async Task<Answer<WriteProposal>> ApplyAsync(string id, CancellationToken cancellationToken)
    {
        var place = copy.Current();
        if (place.Value is not { } current)
            return Answer.Failed<WriteProposal>(place.Error!, place.Hint);

        if (await proposals.FindAsync(id.Trim(), cancellationToken) is not { } proposal)
            return Answer.Failed<WriteProposal>(Messages.NoProposal(id));

        if (!string.Equals(proposal.Task, current.Task, StringComparison.Ordinal))
            return Answer.Failed<WriteProposal>(Messages.OtherTask);

        if (Standing(proposal) is { } refused)
            return refused;

        var found = await sources.FindAsync(proposal.Source, cancellationToken);
        if (found.Value is not { } source)
            return Answer.Failed<WriteProposal>(found.Error!, found.Hint);

        if (!await IsRecordedAsync(proposal, "applying", proposal.Matched, null, cancellationToken))
            return Answer.Failed<WriteProposal>(Messages.JournalDown, Messages.JournalDownHint);

        var written = source.Source.Kind is SourceKind.MongoDb
            ? await documents.WriteAsync(source, proposal, cancellationToken)
            : await tables.WriteAsync(source, proposal, cancellationToken);
        await IsRecordedAsync(proposal, "apply", written.Value, written.Error, cancellationToken);
        if (written.Error is not null)
            return Answer.Failed<WriteProposal>(written.Error, written.Hint);

        var applied = proposal with { State = ProposalState.Applied, Written = written.Value };
        await proposals.SaveAsync(applied, cancellationToken);

        return Answer.Ok(applied);
    }

    /// <summary>The proposal as the dashboard shows it to a person; null when the key is not the server's.</summary>
    public async Task<WriteProposal?> ShowAsync(string id, string? key, CancellationToken cancellationToken) =>
        IsKey(key) ? await proposals.FindAsync(id.Trim(), cancellationToken) : null;

    /// <summary>A person's decision, brought by the dashboard with the server's key; null when the key is not the server's or nothing waits.</summary>
    /// <remarks>A decision the journal cannot hold is not taken: the answer says so, and the proposal still waits.</remarks>
    public async Task<Answer<WriteProposal>?> DecideAsync(string id, string? key, bool isAllowed, string by, string? comment, CancellationToken cancellationToken)
    {
        if (!IsKey(key) || await proposals.FindAsync(id.Trim(), cancellationToken) is not { State: ProposalState.Pending } proposal)
            return null;

        var decided = proposal with
        {
            State = isAllowed ? ProposalState.Approved : ProposalState.Rejected,
            DecidedBy = by,
            DecidedAt = clock.GetUtcNow(),
            Comment = comment
        };
        if (!await IsRecordedAsync(decided, isAllowed ? "approve" : "reject", decided.Matched, null, cancellationToken))
            return Answer.Failed<WriteProposal>(Messages.JournalDown, Messages.JournalDownHint);

        await proposals.SaveAsync(decided, cancellationToken);

        return Answer.Ok(decided);
    }

    private Answer<WriteProposal>? Standing(WriteProposal proposal) => proposal.State switch
    {
        ProposalState.Pending => Answer.Failed<WriteProposal>(Messages.NotDecided, Messages.NotDecidedHint),
        ProposalState.Rejected => Answer.Failed<WriteProposal>(Messages.Refused(proposal.Comment)),
        ProposalState.Applied => Answer.Failed<WriteProposal>(Messages.AlreadyApplied),
        _ when clock.GetUtcNow() - proposal.At > TimeSpan.FromDays(options.Value.ProposalDays) => Answer.Failed<WriteProposal>(Messages.Expired(options.Value.ProposalDays)),
        _ => null
    };

    private async Task<Answer<WriteProposal>> ShapeAsync(WriteContext reached, string collection, WriteOperation operation, string? filter, string? change, CancellationToken cancellationToken)
    {
        if (operation is WriteOperation.Insert)
        {
            var inserted = WriteRules.Documents(change, options.Value.MostWritten);
            if (inserted.Value is not { } added)
                return Answer.Failed<WriteProposal>(inserted.Error!, inserted.Hint);

            return Answer.Ok(Proposal(reached, collection, operation, "{}", added.ToJsonString(), added.Count, []));
        }

        var narrowed = WriteRules.Filter(filter);
        if (narrowed.Value is not { } where)
            return Answer.Failed<WriteProposal>(narrowed.Error!, narrowed.Hint);

        var update = operation is WriteOperation.Delete ? Answer.Ok(new JsonObject()) : WriteRules.Update(change);
        if (update.Value is not { } set)
            return Answer.Failed<WriteProposal>(update.Error!, update.Hint);

        var previewed = await documents.PreviewAsync(reached.Source, collection, where.ToJsonString(), options.Value.ProposalSample, cancellationToken);
        if (previewed.Value is not { } preview)
            return Answer.Failed<WriteProposal>(previewed.Error!, previewed.Hint);

        long touched = operation is WriteOperation.UpdateOne ? Math.Min(1, preview.Matched) : preview.Matched;
        if (touched > options.Value.MostWritten)
            return Answer.Failed<WriteProposal>(Messages.TooMany(touched, options.Value.MostWritten), Messages.TooManyHint);

        return Answer.Ok(Proposal(reached, collection, operation, where.ToJsonString(), set.ToJsonString(), touched, preview.Sample));
    }

    /// <summary>A write to a table: columns and values checked here, the statement written by the adapter, the rows counted by the reader.</summary>
    private async Task<Answer<WriteProposal>> ShapeTableAsync(WriteContext reached, string table, WriteOperation operation, string? filter, string? change, CancellationToken cancellationToken)
    {
        if (operation is WriteOperation.Insert)
        {
            var inserted = SqlWriteRules.Rows(change, options.Value.MostWritten);
            if (inserted.Value is not { } added)
                return Answer.Failed<WriteProposal>(inserted.Error!, inserted.Hint);

            return Answer.Ok(Proposal(reached, table, operation, "{}", added.ToJsonString(), added.Count, []));
        }

        var narrowed = SqlWriteRules.Filter(filter);
        if (narrowed.Value is not { } where)
            return Answer.Failed<WriteProposal>(narrowed.Error!, narrowed.Hint);

        var update = operation is WriteOperation.Delete ? Answer.Ok(new JsonObject()) : SqlWriteRules.Update(change);
        if (update.Value is not { } set)
            return Answer.Failed<WriteProposal>(update.Error!, update.Hint);

        var previewed = await tables.PreviewAsync(reached.Source, table, where.ToJsonString(), options.Value.ProposalSample, cancellationToken);
        if (previewed.Value is not { } preview)
            return Answer.Failed<WriteProposal>(previewed.Error!, previewed.Hint);

        if (operation is WriteOperation.UpdateOne && preview.Matched != 1)
            return Answer.Failed<WriteProposal>(Messages.NotOne(preview.Matched), Messages.NotOneHint);

        if (preview.Matched > options.Value.MostWritten)
            return Answer.Failed<WriteProposal>(Messages.TooMany(preview.Matched, options.Value.MostWritten), Messages.TooManyHint);

        return Answer.Ok(Proposal(reached, table, operation, where.ToJsonString(), set.ToJsonString(), preview.Matched, preview.Sample));
    }

    private WriteProposal Proposal(WriteContext reached, string collection, WriteOperation operation, string filter, string change, long matched, IReadOnlyList<string> sample) => new()
    {
        Id = $"w{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8))}",
        Task = reached.Copy.Task,
        Source = reached.Source.Source.Name,
        Collection = collection,
        Operation = operation,
        Filter = filter,
        Change = change,
        Reason = string.Empty,
        Matched = matched,
        Sample = sample,
        At = clock.GetUtcNow()
    };

    private async Task<Answer<WriteContext>> ReachAsync(string name, string collection, CancellationToken cancellationToken)
    {
        var place = copy.Current();
        if (place.Value is not { } current)
            return Answer.Failed<WriteContext>(place.Error!, place.Hint);

        var found = await sources.FindAsync(name.Trim(), cancellationToken);
        if (found.Value is not { } source)
            return Answer.Failed<WriteContext>(found.Error!, found.Hint);

        if (source.Source.Kind is not SourceKind.MongoDb && !SqlWriteRules.IsTable(collection))
            return Answer.Failed<WriteContext>(Messages.SqlTableBad(collection), Messages.CollectionsHint(source.Source));

        bool isAllowed = source.Source.Kind is SourceKind.MongoDb ? source.Source.Allows(collection) : SqlRules.IsAllowed(source.Source, collection);
        if (!isAllowed)
            return Answer.Failed<WriteContext>(Messages.CollectionKept(source.Source.Name, collection), Messages.CollectionsHint(source.Source));

        return Answer.Ok(new WriteContext(source, current));
    }

    /// <summary>Writes one step of a write into the journal; false when the journal could not hold it.</summary>
    private async Task<bool> IsRecordedAsync(WriteProposal proposal, string operation, long rows, string? error, CancellationToken cancellationToken)
    {
        string query = new JsonObject
        {
            ["proposal"] = proposal.Id,
            ["operation"] = WriteRules.Word(proposal.Operation),
            ["filter"] = JsonNode.Parse(proposal.Filter),
            ["change"] = JsonNode.Parse(proposal.Change),
            ["by"] = proposal.DecidedBy
        }.ToJsonString();

        var recorded = await journal.WriteAsync(new QueryRecord(clock.GetUtcNow(), proposal.Task, proposal.Source, proposal.Collection, operation, query, rows, 0, error), cancellationToken);

        return recorded.Error is null;
    }

    private bool IsKey(string? key) =>
        !string.IsNullOrEmpty(options.Value.ApprovalKey) && key is not null
        && CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(key), System.Text.Encoding.UTF8.GetBytes(options.Value.ApprovalKey));

    /// <summary>The source a write goes to, and the copy of the task that proposes it.</summary>
    private sealed record WriteContext(ReachableSource Source, CopyPlace Copy);
}
