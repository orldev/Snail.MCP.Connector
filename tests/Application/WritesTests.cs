using Microsoft.Extensions.Time.Testing;
using Snail.MCP.Connector.Application;

namespace Snail.MCP.Connector.Tests.Application;

/// <summary>
/// Writes as the owner decided them: proposed and dry-run by the agent, allowed only through the dashboard's key, run once
/// for the task that proposed them, every step in the journal.
/// </summary>
public sealed class WritesTests
{
    private const string Key = "key-of-the-stand";

    private readonly Fakes _fakes = new();

    [Fact]
    public async Task ProposeAsync_Update_DryRunsKeepsItAndWritesNothing()
    {
        var proposed = await _fakes.Writes().ProposeAsync("crm", "deals", "update", """{"stage": "lead"}""", """{"$set": {"stage": "won"}}""", "the deal was signed", CancellationToken.None);

        var proposal = proposed.Value!;
        Assert.Equal(ProposalState.Pending, proposal.State);
        Assert.Equal(3, proposal.Matched);
        Assert.Single(proposal.Sample);
        Assert.Same(proposal, _fakes.Kept.All[proposal.Id]);
        Assert.Empty(_fakes.Database.WrittenProposals);
        Assert.Equal("propose", Assert.Single(_fakes.Written.Records).Operation);
    }

    /// <summary>An empty filter would touch every document; a person might still press yes, so it never reaches them.</summary>
    [Theory]
    [InlineData("update", null, """{"$set": {"stage": "won"}}""")]
    [InlineData("delete", "{}", null)]
    public async Task ProposeAsync_EmptyFilter_IsRefusedBeforeTheDatabase(string operation, string? filter, string? change)
    {
        var proposed = await _fakes.Writes().ProposeAsync("crm", "deals", operation, filter, change, "clean up", CancellationToken.None);

        Assert.Equal(Messages.EmptyFilter, proposed.Error);
        Assert.Empty(_fakes.Database.Asked);
    }

    /// <summary>A replacement document would drop every field it does not name.</summary>
    [Fact]
    public async Task ProposeAsync_UpdateThatIsAReplacement_IsRefused()
    {
        var proposed = await _fakes.Writes().ProposeAsync("crm", "deals", "update", """{"_id": 1}""", """{"stage": "won"}""", "fix", CancellationToken.None);

        Assert.Equal(Messages.BadUpdate("stage"), proposed.Error);
    }

    [Fact]
    public async Task ProposeAsync_MoreThanTheMost_IsRefusedToBeNarrowed()
    {
        _fakes.Database.Matched = 5000;

        var proposed = await _fakes.Writes().ProposeAsync("crm", "deals", "delete", """{"stage": "lost"}""", null, "clean up", CancellationToken.None);

        Assert.Equal(Messages.TooMany(5000, 1000), proposed.Error);
    }

    [Fact]
    public async Task ProposeAsync_SourceWithoutAWriter_IsRefused()
    {
        var proposed = await _fakes.Writes().ProposeAsync("sales", "orders", "insert", null, """[{"n": 1}]""", "add", CancellationToken.None);

        Assert.Equal(Messages.NoWriter, proposed.Error);
    }

    [Fact]
    public async Task ProposeAsync_SqlUpdate_DryRunsByColumnsAndKeepsIt()
    {
        var proposed = await _fakes.Writes().ProposeAsync("erp", "invoices", "update", """{"id": 7}""", """{"$set": {"status": "paid"}}""", "paid on the 3rd", CancellationToken.None);

        Assert.Equal(1, proposed.Value!.Matched);
        Assert.Equal("""{"id":7}""", Assert.Single(_fakes.Tables.Asked));
        Assert.Empty(_fakes.Tables.WrittenProposals);
        Assert.Single(_fakes.Kept.All);
    }

    [Theory]
    [InlineData("""{"id": {"$regex": "7"}}""", """{"$set": {"status": "paid"}}""")]
    [InlineData("""{"id; drop table x": 7}""", """{"$set": {"status": "paid"}}""")]
    [InlineData("""{}""", """{"$set": {"status": "paid"}}""")]
    [InlineData("""{"id": 7}""", """{"$unset": {"status": ""}}""")]
    [InlineData("""{"id": 7}""", """{"$inc": {"amount": "10"}}""")]
    [InlineData("""{"id": 7}""", """{"$set": {"status": {"nested": true}}}""")]
    public async Task ProposeAsync_SqlWriteThatIsNotColumnsAndValues_IsRefusedBeforeTheDatabase(string filter, string change)
    {
        var proposed = await _fakes.Writes().ProposeAsync("erp", "invoices", "update", filter, change, "fix", CancellationToken.None);

        Assert.NotNull(proposed.Error);
        Assert.Empty(_fakes.Tables.Asked);
    }

    [Fact]
    public async Task ProposeAsync_SqlUpdateOneMatchingSeveral_AsksForTheKey()
    {
        _fakes.Tables.Matched = 4;

        var proposed = await _fakes.Writes().ProposeAsync("erp", "invoices", "update_one", """{"status": "open"}""", """{"$set": {"status": "paid"}}""", "fix", CancellationToken.None);

        Assert.Equal(Messages.NotOne(4), proposed.Error);
        Assert.Equal(Messages.NotOneHint, proposed.Hint);
    }

    [Theory]
    [InlineData("salaries")]
    [InlineData("invoices; DROP TABLE invoices")]
    public async Task ProposeAsync_TableTheSourceKeepsOrNoName_IsRefused(string table)
    {
        var proposed = await _fakes.Writes().ProposeAsync("erp", table, "delete", """{"id": 7}""", null, "fix", CancellationToken.None);

        Assert.NotNull(proposed.Error);
        Assert.Empty(_fakes.Tables.Asked);
    }

    [Fact]
    public async Task ApplyAsync_AllowedSqlWrite_RunsThroughTheTables()
    {
        var proposal = (await _fakes.Writes().ProposeAsync("erp", "invoices", "delete", """{"id": 7}""", null, "duplicate", CancellationToken.None)).Value!;
        await _fakes.Writes().DecideAsync(proposal.Id, Key, isAllowed: true, "owner", null, CancellationToken.None);

        var applied = await _fakes.Writes().ApplyAsync(proposal.Id, CancellationToken.None);

        Assert.Equal(1, applied.Value!.Written);
        Assert.Equal(WriteOperation.Delete, Assert.Single(_fakes.Tables.WrittenProposals).Operation);
        Assert.Empty(_fakes.Database.WrittenProposals);
    }

    [Fact]
    public async Task ProposeAsync_ServerWithoutAnApprovalKey_KeepsWritesOff()
    {
        _fakes.Settings.ApprovalKey = string.Empty;

        var proposed = await _fakes.Writes().ProposeAsync("crm", "deals", "insert", null, """[{"n": 1}]""", "add", CancellationToken.None);

        Assert.Equal(Messages.NoApprovals, proposed.Error);
    }

    [Fact]
    public async Task ApplyAsync_NobodyDecided_WritesNothing()
    {
        var proposal = await ProposedAsync();

        var applied = await _fakes.Writes().ApplyAsync(proposal.Id, CancellationToken.None);

        Assert.Equal(Messages.NotDecided, applied.Error);
        Assert.Empty(_fakes.Database.WrittenProposals);
    }

    /// <summary>The decision is a person's only when it comes with the server's key, which no agent ever holds.</summary>
    [Fact]
    public async Task DecideAsync_WrongKey_DecidesNothing()
    {
        var proposal = await ProposedAsync();

        var decided = await _fakes.Writes().DecideAsync(proposal.Id, "guessed", isAllowed: true, "agent", null, CancellationToken.None);

        Assert.Null(decided);
        Assert.Equal(ProposalState.Pending, _fakes.Kept.All[proposal.Id].State);
    }

    [Fact]
    public async Task ApplyAsync_Allowed_WritesOnceAndRecordsIt()
    {
        var proposal = await ProposedAsync();
        await _fakes.Writes().DecideAsync(proposal.Id, Key, isAllowed: true, "owner", "ok", CancellationToken.None);

        var applied = await _fakes.Writes().ApplyAsync(proposal.Id, CancellationToken.None);
        var again = await _fakes.Writes().ApplyAsync(proposal.Id, CancellationToken.None);

        Assert.Equal(ProposalState.Applied, applied.Value!.State);
        Assert.Equal(3, applied.Value.Written);
        Assert.Single(_fakes.Database.WrittenProposals);
        Assert.Equal(Messages.AlreadyApplied, again.Error);
        Assert.Equal(["propose", "approve", "applying", "apply"], _fakes.Written.Records.Select(record => record.Operation));
    }

    [Fact]
    public async Task ApplyAsync_Refused_SaysWhatThePersonSaid()
    {
        var proposal = await ProposedAsync();
        await _fakes.Writes().DecideAsync(proposal.Id, Key, isAllowed: false, "owner", "not this quarter", CancellationToken.None);

        var applied = await _fakes.Writes().ApplyAsync(proposal.Id, CancellationToken.None);

        Assert.Equal(Messages.Refused("not this quarter"), applied.Error);
    }

    /// <summary>Only the task that proposed the write may run it: another task holding the id runs nothing.</summary>
    [Fact]
    public async Task ApplyAsync_AnotherTask_IsRefused()
    {
        var proposal = await ProposedAsync();
        await _fakes.Writes().DecideAsync(proposal.Id, Key, isAllowed: true, "owner", null, CancellationToken.None);
        _fakes.Root = "/workspaces/other-task/step-0";

        var applied = await _fakes.Writes().ApplyAsync(proposal.Id, CancellationToken.None);

        Assert.Equal(Messages.OtherTask, applied.Error);
        Assert.Empty(_fakes.Database.WrittenProposals);
    }

    [Fact]
    public async Task ApplyAsync_AllowedButPastItsDays_IsRefused()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var proposal = (await _fakes.Writes(clock).ProposeAsync("crm", "deals", "insert", null, """[{"n": 1}]""", "add", CancellationToken.None)).Value!;
        await _fakes.Writes(clock).DecideAsync(proposal.Id, Key, isAllowed: true, "owner", null, CancellationToken.None);
        clock.Advance(TimeSpan.FromDays(8));

        var applied = await _fakes.Writes(clock).ApplyAsync(proposal.Id, CancellationToken.None);

        Assert.Equal(Messages.Expired(7), applied.Error);
    }

    /// <summary>No change to a company's data goes unrecorded: with the journal down nothing is proposed, decided or written.</summary>
    [Fact]
    public async Task ProposeAsync_JournalDown_KeepsNoProposal()
    {
        _fakes.Written.IsDown = true;

        var proposed = await _fakes.Writes().ProposeAsync("crm", "deals", "update", """{"stage": "lead"}""", """{"$set": {"stage": "won"}}""", "signed", CancellationToken.None);

        Assert.Equal(Messages.JournalDown, proposed.Error);
        Assert.Empty(_fakes.Kept.All);
    }

    [Fact]
    public async Task DecideAsync_JournalDown_LeavesTheProposalWaiting()
    {
        var proposal = await ProposedAsync();
        _fakes.Written.IsDown = true;

        var decided = await _fakes.Writes().DecideAsync(proposal.Id, Key, isAllowed: true, "owner", null, CancellationToken.None);

        Assert.Equal(Messages.JournalDown, decided!.Error);
        Assert.Equal(ProposalState.Pending, _fakes.Kept.All[proposal.Id].State);
    }

    [Fact]
    public async Task ApplyAsync_JournalDown_WritesNothing()
    {
        var proposal = await ProposedAsync();
        await _fakes.Writes().DecideAsync(proposal.Id, Key, isAllowed: true, "owner", null, CancellationToken.None);
        _fakes.Written.IsDown = true;

        var applied = await _fakes.Writes().ApplyAsync(proposal.Id, CancellationToken.None);

        Assert.Equal(Messages.JournalDown, applied.Error);
        Assert.Empty(_fakes.Database.WrittenProposals);
    }

    private async Task<WriteProposal> ProposedAsync() =>
        (await _fakes.Writes().ProposeAsync("crm", "deals", "update", """{"stage": "lead"}""", """{"$set": {"stage": "won"}}""", "signed", CancellationToken.None)).Value!;
}
