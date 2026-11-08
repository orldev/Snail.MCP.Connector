using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Snail.MCP.Connector.Application.Ports;
using Snail.MCP.Connector.Configuration;
using Snail.Sdk.Mcp.Hosting.Sessions;

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
    IOptions<ConnectorOptions> options) : IDisposable
{
    private readonly SemaphoreSlim _deciding = new(1, 1);

    /// <summary>Dry-runs the write and keeps it as a proposal for a person to decide.</summary>
    public async Task<Answer<WriteProposal>> ProposeAsync(string source, string collection, string operation, string? filter, string? change, string reason, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.Value.ApprovalKey))
            return Answer.Failed<WriteProposal>(Messages.NoApprovals, Messages.NoApprovalsHint);

        var context = await ReachAsync(source, collection, cancellationToken);
        if (!context.TryGet(out var reached))
            return context.As<WriteProposal>();

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
    /// <remarks>
    /// The proposal is taken — saved as applying — before the database is asked, under the lock every decision takes, so
    /// a second call made at the same time finds it taken. What the write ended in is saved whatever became of the call
    /// that asked: a write the database made is never left looking approved. A call cut off during the write leaves the
    /// proposal applying, which nothing runs again until a person looks at the data.
    /// </remarks>
    public async Task<Answer<WriteProposal>> ApplyAsync(string id, CancellationToken cancellationToken)
    {
        var place = copy.Current();
        if (!place.TryGet(out var current))
            return place.As<WriteProposal>();

        var claimed = await ClaimAsync(id.Trim(), current.Task, cancellationToken);
        if (claimed.Value is not { } proposal)
            return claimed;

        var written = await WrittenAsync(proposal, cancellationToken);
        await IsRecordedAsync(proposal, "apply", written.Value, written.Error, CancellationToken.None);

        var settled = written.Error is null
            ? proposal with { State = ProposalState.Applied, Written = written.Value }
            : proposal with { State = ProposalState.Approved };
        await proposals.SaveAsync(settled, CancellationToken.None);

        return written.Error is null ? Answer.Ok(settled) : Answer.Failed<WriteProposal>(written.Error, written.Hint);
    }

    /// <summary>The proposal as the dashboard shows it to a person; null when the key is not the server's.</summary>
    public async Task<WriteProposal?> ShowAsync(string id, string? key, CancellationToken cancellationToken) =>
        IsKey(key) ? await proposals.FindAsync(id.Trim(), cancellationToken) : null;

    /// <summary>A person's decision, brought by the dashboard with the server's key; null when the key is not the server's or nothing waits.</summary>
    /// <remarks>A decision the journal cannot hold is not taken: the answer says so, and the proposal still waits.</remarks>
    public async Task<Answer<WriteProposal>?> DecideAsync(string id, string? key, bool isAllowed, string by, string? comment, CancellationToken cancellationToken)
    {
        if (!IsKey(key))
            return null;

        await _deciding.WaitAsync(cancellationToken);
        try
        {
            if (await proposals.FindAsync(id.Trim(), cancellationToken) is not { State: ProposalState.Pending } proposal)
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

            await proposals.SaveAsync(decided, CancellationToken.None);

            return Answer.Ok(decided);
        }
        finally
        {
            _deciding.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _deciding.Dispose();

    /// <summary>The proposal taken to run: this task's, allowed, journaled as applying and saved so, or why it cannot be taken.</summary>
    private async Task<Answer<WriteProposal>> ClaimAsync(string id, string task, CancellationToken cancellationToken)
    {
        await _deciding.WaitAsync(cancellationToken);
        try
        {
            if (await proposals.FindAsync(id, cancellationToken) is not { } proposal)
                return Answer.Failed<WriteProposal>(Messages.NoProposal(id));

            if (!string.Equals(proposal.Task, task, StringComparison.Ordinal))
                return Answer.Failed<WriteProposal>(Messages.OtherTask);

            if (Standing(proposal) is { } refused)
                return refused;

            if (!await IsRecordedAsync(proposal, "applying", proposal.Matched, null, cancellationToken))
                return Answer.Failed<WriteProposal>(Messages.JournalDown, Messages.JournalDownHint);

            var applying = proposal with { State = ProposalState.Applying };
            await proposals.SaveAsync(applying, CancellationToken.None);

            return Answer.Ok(applying);
        }
        finally
        {
            _deciding.Release();
        }
    }

    private async Task<Answer<long>> WrittenAsync(WriteProposal proposal, CancellationToken cancellationToken)
    {
        var found = await sources.FindAsync(proposal.Source, cancellationToken);
        if (!found.TryGet(out var source))
            return found.As<long>();

        return source.Source.Kind is SourceKind.MongoDb
            ? await documents.WriteAsync(source, proposal, cancellationToken)
            : await tables.WriteAsync(source, proposal, cancellationToken);
    }

    private Answer<WriteProposal>? Standing(WriteProposal proposal) => proposal.State switch
    {
        ProposalState.Pending => Answer.Failed<WriteProposal>(Messages.NotDecided, Messages.NotDecidedHint),
        ProposalState.Rejected => Answer.Failed<WriteProposal>(Messages.Refused(proposal.Comment)),
        ProposalState.Applied => Answer.Failed<WriteProposal>(Messages.AlreadyApplied),
        ProposalState.Applying => Answer.Failed<WriteProposal>(Messages.BeingApplied, Messages.BeingAppliedHint),
        _ when clock.GetUtcNow() - proposal.At > TimeSpan.FromDays(options.Value.ProposalDays) => Answer.Failed<WriteProposal>(Messages.Expired(options.Value.ProposalDays)),
        _ => null
    };

    private async Task<Answer<WriteProposal>> ShapeAsync(WriteContext reached, string collection, WriteOperation operation, string? filter, string? change, CancellationToken cancellationToken)
    {
        if (operation is WriteOperation.Insert)
        {
            var inserted = WriteRules.Documents(change, options.Value.MostWritten);
            if (!inserted.TryGet(out var added))
                return inserted.As<WriteProposal>();

            return Answer.Ok(Proposal(reached, collection, operation, "{}", added.ToJsonString(), added.Count, []));
        }

        var narrowed = WriteRules.Filter(filter);
        if (!narrowed.TryGet(out var where))
            return narrowed.As<WriteProposal>();

        var update = operation is WriteOperation.Delete ? Answer.Ok(new JsonObject()) : WriteRules.Update(change);
        if (!update.TryGet(out var set))
            return update.As<WriteProposal>();

        var previewed = await documents.PreviewAsync(reached.Source, collection, where.ToJsonString(), options.Value.ProposalSample, cancellationToken);
        if (!previewed.TryGet(out var preview))
            return previewed.As<WriteProposal>();

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
            if (!inserted.TryGet(out var added))
                return inserted.As<WriteProposal>();

            return Answer.Ok(Proposal(reached, table, operation, "{}", added.ToJsonString(), added.Count, []));
        }

        var narrowed = SqlWriteRules.Filter(filter);
        if (!narrowed.TryGet(out var where))
            return narrowed.As<WriteProposal>();

        var update = operation is WriteOperation.Delete ? Answer.Ok(new JsonObject()) : SqlWriteRules.Update(change);
        if (!update.TryGet(out var set))
            return update.As<WriteProposal>();

        var previewed = await tables.PreviewAsync(reached.Source, table, where.ToJsonString(), options.Value.ProposalSample, cancellationToken);
        if (!previewed.TryGet(out var preview))
            return previewed.As<WriteProposal>();

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
        if (!place.TryGet(out var current))
            return place.As<WriteContext>();

        var found = await sources.FindAsync(name.Trim(), cancellationToken);
        if (!found.TryGet(out var source))
            return found.As<WriteContext>();

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
    private sealed record WriteContext(ReachableSource Source, WorkspacePlace<string> Copy);
}
