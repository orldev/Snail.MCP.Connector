using Snail.MCP.Connector.Application;
using Snail.Sdk.Mcp.Hosting.Sessions;

namespace Snail.MCP.Connector.Tests.Application;

/// <summary>
/// The queries as the tools make them: each one written into the journal under the session's task, refused before the
/// database when the rules or the source say no, and withheld when the journal cannot be written.
/// </summary>
public sealed class QueriesTests
{
    private readonly Fakes _fakes = new();

    [Fact]
    public async Task FindAsync_AllowedCollection_ReadsAndWritesTheQueryIntoTheJournal()
    {
        var found = await _fakes.Queries().FindAsync("sales", "orders", """{"status": "paid"}""", null, null, null, CancellationToken.None);

        Assert.Equal(2, found.Value!.Rows.Count);
        var record = Assert.Single(_fakes.Written.Records);
        Assert.Equal(Fakes.TaskId, record.Task);
        Assert.Equal("sales", record.Source);
        Assert.Equal("orders", record.Collection);
        Assert.Equal("find", record.Operation);
        Assert.Contains("paid", record.Query, StringComparison.Ordinal);
        Assert.Equal(2, record.Rows);
    }

    /// <summary>The source keeps the collection out: the database is never asked.</summary>
    [Fact]
    public async Task FindAsync_CollectionTheSourceKeeps_IsRefusedBeforeTheDatabase()
    {
        var found = await _fakes.Queries().FindAsync("sales", "salaries", null, null, null, null, CancellationToken.None);

        Assert.Equal(Messages.CollectionKept("sales", "salaries"), found.Error);
        Assert.Empty(_fakes.Database.Asked);
    }

    [Fact]
    public async Task FindAsync_LimitAboveTheMost_IsHeldToTheMost()
    {
        await _fakes.Queries().FindAsync("sales", "orders", null, null, null, 100000, CancellationToken.None);

        Assert.Equal(200, _fakes.Database.LastQuery!.Limit.Rows);
    }

    /// <summary>No data of the company reaches an agent unrecorded: an answer whose query could not be written down is withheld.</summary>
    [Fact]
    public async Task FindAsync_JournalCannotBeWritten_WithholdsTheAnswer()
    {
        _fakes.Written.IsDown = true;

        var found = await _fakes.Queries().FindAsync("sales", "orders", null, null, null, null, CancellationToken.None);

        Assert.Null(found.Value);
        Assert.Equal(Messages.JournalDown, found.Error);
    }

    /// <summary>A query nobody can trace to a task is the one the journal exists to rule out.</summary>
    [Fact]
    public async Task CountAsync_SessionWithoutACopy_IsRefused()
    {
        _fakes.Root = null;

        var counted = await _fakes.Queries().CountAsync("sales", "orders", null, CancellationToken.None);

        Assert.Equal(Messages.NoCopy, counted.Error);
        Assert.Empty(_fakes.Database.Asked);
    }

    /// <summary>Every reason the SDK gives for a root that names no task's copy, in this server's words.</summary>
    [Theory]
    [InlineData("", Messages.NoCopy, "noRoot")]
    [InlineData("   ", Messages.NoCopy, "noRoot")]
    [InlineData("/elsewhere/task-1/step-0", "is not a task's copy", "outside")]
    [InlineData("/workspaces", "is not a task's copy", "outside")]
    [InlineData("/workspaces/task-1/../../etc", "is not a task's copy", "malformed")]
    [InlineData("/workspaces/Not_A_Task/step-0", "is not a task's copy", "notATask")]
    public void Of_RootThatNamesNoCopy_IsRefusedInTheServersWords(string root, string said, string type)
    {
        var place = SessionCopy.Of(root, "/workspaces");

        Assert.False(place.IsSuccess);
        Assert.Contains(said, place.Error, StringComparison.Ordinal);
        Assert.Equal(type, place.Type);
    }

    [Fact]
    public void Of_TaskCopy_IsTheTaskAndTheCopy()
    {
        var place = SessionCopy.Of("/workspaces/task-1/step-3", "/workspaces");

        Assert.Equal(new WorkspacePlace<string>("task-1", "/workspaces/task-1/step-3"), place.Value);
    }

    [Fact]
    public async Task AggregateAsync_PipelineThatWrites_IsRefusedAndNeverRuns()
    {
        var aggregated = await _fakes.Queries().AggregateAsync("sales", "orders", """[{"$out": "copy"}]""", null, CancellationToken.None);

        Assert.Equal(Messages.Forbidden("$out"), aggregated.Error);
        Assert.Empty(_fakes.Database.Asked);
    }

    [Fact]
    public async Task CollectionsAsync_SourceAllowingSome_ListsOnlyThoseThatExist()
    {
        var listed = await _fakes.Queries().CollectionsAsync("sales", CancellationToken.None);

        Assert.Equal(["customers", "orders"], listed.Value!.Collections);
    }

    [Fact]
    public async Task CountAsync_SqlSource_SaysToUseASelect()
    {
        var counted = await _fakes.Queries().CountAsync("ledger", "accounts", null, CancellationToken.None);

        Assert.Equal(Messages.NotDocuments, counted.Error);
    }

    [Fact]
    public async Task QueryAsync_SqlSource_RunsTheSelectAndJournalsIt()
    {
        var queried = await _fakes.Queries().QueryAsync("ledger", "SELECT count(*) FROM accounts", null, CancellationToken.None);

        Assert.Single(queried.Value!.Rows);
        Assert.Equal(["SELECT count(*) FROM accounts"], _fakes.Tables.Asked);
        var record = Assert.Single(_fakes.Written.Records);
        Assert.Equal("query", record.Operation);
        Assert.Equal("accounts", record.Collection);
    }

    [Fact]
    public async Task QueryAsync_MongoSource_SaysToUseFindAndPipelines()
    {
        var queried = await _fakes.Queries().QueryAsync("sales", "SELECT 1", null, CancellationToken.None);

        Assert.Equal(Messages.NotTables, queried.Error);
    }

    [Fact]
    public async Task QueryAsync_StatementThatWrites_IsRefusedBeforeTheDatabase()
    {
        var queried = await _fakes.Queries().QueryAsync("ledger", "UPDATE accounts SET balance = 0", null, CancellationToken.None);

        Assert.Equal(Messages.NotASelect, queried.Error);
        Assert.Empty(_fakes.Tables.Asked);
    }

    [Fact]
    public async Task ExportAsync_SqlSource_WritesTheRowsOfTheSelect()
    {
        var exported = await _fakes.Queries().ExportAsync("ledger", null, null, null, "SELECT * FROM accounts", "csv", "accounts", CancellationToken.None);

        Assert.Equal("exports/accounts.csv", exported.Value!.Path);
        Assert.Equal(["SELECT * FROM accounts"], _fakes.Tables.Asked);
    }

    [Fact]
    public async Task CountAsync_NoSuchSource_SaysWhereTheNamesAre()
    {
        var counted = await _fakes.Queries().CountAsync("ghost", "deals", null, CancellationToken.None);

        Assert.Equal(Messages.NoSource("ghost"), counted.Error);
        Assert.Equal(Messages.NoSourceHint, counted.Hint);
    }

    [Theory]
    [InlineData("""{"status": "paid"}""", """[{"$match": {}}]""")]
    [InlineData(null, null)]
    public async Task ExportAsync_BothOrNeitherQuery_IsRefused(string? filter, string? pipeline)
    {
        var exported = await _fakes.Queries().ExportAsync("sales", "orders", filter, pipeline, null, "csv", "paid", CancellationToken.None);

        Assert.Equal(Messages.OneQuery, exported.Error);
    }

    [Theory]
    [InlineData("../paid.csv")]
    [InlineData("exports/paid")]
    [InlineData("")]
    public async Task ExportAsync_FileNameWithAFolder_IsRefused(string file)
    {
        var exported = await _fakes.Queries().ExportAsync("sales", "orders", "{}", null, null, "csv", file, CancellationToken.None);

        Assert.Equal(Messages.BadFileName, exported.Error);
    }

    [Fact]
    public async Task ExportAsync_Filter_WritesTheFileAndJournalsTheExport()
    {
        var exported = await _fakes.Queries().ExportAsync("sales", "orders", """{"status": "paid"}""", null, null, "xlsx", "paid", CancellationToken.None);

        Assert.Equal("exports/paid.xlsx", exported.Value!.Path);
        Assert.Equal(["exporting", "export"], _fakes.Written.Records.Select(record => record.Operation));
    }

    [Fact]
    public async Task ExportAsync_FormatItDoesNotWrite_IsRefused()
    {
        var exported = await _fakes.Queries().ExportAsync("sales", "orders", "{}", null, null, "pdf", "paid", CancellationToken.None);

        Assert.Equal(Messages.BadFormat, exported.Error);
    }

    [Fact]
    public async Task JournalAsync_AfterTwoQueries_ReadsThemNewestFirst()
    {
        var queries = _fakes.Queries();
        await queries.CountAsync("sales", "orders", null, CancellationToken.None);
        await queries.FindAsync("sales", "customers", null, null, null, null, CancellationToken.None);

        var read = await queries.JournalAsync(10, CancellationToken.None);

        Assert.Equal(["find", "count"], read.Value!.Select(record => record.Operation));
    }
}
