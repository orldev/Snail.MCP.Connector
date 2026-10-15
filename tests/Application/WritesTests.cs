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
        Assert.Equal(["propose", "approve", "apply"], _fakes.Written.Records.Select(record => record.Operation));
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

    private async Task<WriteProposal> ProposedAsync() =>
        (await _fakes.Writes().ProposeAsync("crm", "deals", "update", """{"stage": "lead"}""", """{"$set": {"stage": "won"}}""", "signed", CancellationToken.None)).Value!;
}
