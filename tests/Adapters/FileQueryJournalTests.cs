using Microsoft.Extensions.Options;
using Snail.MCP.Connector.Adapters;
using Snail.MCP.Connector.Configuration;

namespace Snail.MCP.Connector.Tests.Adapters;

/// <summary>The journal of queries: a file a day, read back per task, old days deleted.</summary>
public sealed class FileQueryJournalTests : IDisposable
{
    private readonly string _folder = TestFolder.Create();

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public async Task ReadAsync_QueriesOfTwoTasks_ReadsOnlyTheOneAskedNewestFirst()
    {
        using var journal = Journal(365);
        var now = DateTimeOffset.UtcNow;
        await journal.WriteAsync(Record(now, "task-a", "count"), CancellationToken.None);
        await journal.WriteAsync(Record(now, "task-b", "find"), CancellationToken.None);
        await journal.WriteAsync(Record(now, "task-a", "aggregate"), CancellationToken.None);

        var read = await journal.ReadAsync("task-a", 10, CancellationToken.None);

        Assert.Equal(["aggregate", "count"], read.Select(record => record.Operation));
    }

    /// <summary>Retention is the company's: a day older than the days kept goes with the next write.</summary>
    [Fact]
    public async Task WriteAsync_DayOlderThanKept_DeletesItsFile()
    {
        using var journal = Journal(30);
        var now = DateTimeOffset.UtcNow;
        await journal.WriteAsync(Record(now.AddDays(-40), "task-a", "count"), CancellationToken.None);

        await journal.WriteAsync(Record(now, "task-a", "find"), CancellationToken.None);

        Assert.Single(Directory.EnumerateFiles(_folder));
    }

    private FileQueryJournal Journal(int days) => new(_folder, Options.Create(new ConnectorOptions { JournalDays = days }));

    private static QueryRecord Record(DateTimeOffset at, string task, string operation) => new(at, task, "sales", "orders", operation, "{}", 1, 5, null);
}
