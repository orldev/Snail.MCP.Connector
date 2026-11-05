using MongoDB.Bson;
using MongoDB.Driver;
using Snail.MCP.Connector.Adapters;
using Snail.MCP.Connector.Application;
using Snail.MCP.Connector.Application.Ports;
using Snail.MCP.Connector.Configuration;
using Snail.MCP.Connector.Tests.Adapters;
using Snail.MCP.Connector.Tests.Application;

namespace Snail.MCP.Connector.Tests.Chaos;

/// <summary>"Every query is written down, whether it succeeded or not" — when an adapter breaks its own promise to answer in data.</summary>
public sealed class JournalChaosTests
{
    /// <summary>
    /// <see cref="Queries"/> writes the journal line after the adapter answers. An adapter that throws — the value
    /// Npgsql cannot read, a document too deep for the JSON reader — leaves the query unrecorded: the one query the
    /// company would want to see is the one that is missing.
    /// </summary>
    [Fact]
    public async Task QueryAsync_AdapterThatThrows_IsStillJournaled()
    {
        var fakes = new Fakes();
        var queries = Queries(fakes, new ThrowingTables());

        await Record.ExceptionAsync(() => queries.QueryAsync("ledger", "SELECT age(issued) FROM invoices", null, CancellationToken.None));

        Assert.Single(fakes.Written.Records);
    }

    /// <summary>
    /// <see cref="FileQueryJournal.ReadAsync"/> reads day files newest first and stops once it has found enough lines of the
    /// task. A task with fewer lines than asked — every new task — makes it read and parse the whole journal, up to a year
    /// of every task on the stand, on each connector_journal call. Thirty busy days here: 600 000 lines, 20 000 a day.
    /// </summary>
    [Fact]
    public async Task ReadAsync_TaskWithFewLinesInABusyJournal_AnswersWithinASecond()
    {
        string folder = TestFolder.Create();
        try
        {
            var today = DateTimeOffset.UtcNow;
            for (int day = 0; day < 30; day++)
            {
                var at = today.AddDays(-day);
                var lines = Enumerable.Range(0, 20_000)
                    .Select(index => new QueryRecord(at, $"task-{index % 50}", "sales", "orders", "find", """{"filter":{"status":"paid"},"limit":50}""", 50, 12, null))
                    .Select(record => System.Text.Json.JsonSerializer.Serialize(record, JournalJson.Default.QueryRecord));
                await File.WriteAllLinesAsync(Path.Combine(folder, $"{at.UtcDateTime:yyyy-MM-dd}.jsonl"), lines);
            }

            var journal = new FileQueryJournal(folder, Microsoft.Extensions.Options.Options.Create(new ConnectorOptions()));
            var watch = System.Diagnostics.Stopwatch.StartNew();

            await journal.ReadAsync("lonely-task", 20, CancellationToken.None);

            watch.Stop();
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"took {watch.Elapsed.TotalSeconds:0.0} s");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static Queries Queries(Fakes fakes, ITables tables)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new ConnectorOptions());
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.QueryString = new Microsoft.AspNetCore.Http.QueryString($"?root={fakes.Root}");
        var copy = new SessionCopy(new Microsoft.AspNetCore.Http.HttpContextAccessor { HttpContext = context }, options);

        return new Queries(new Fakes.SourceList(fakes.Sources), fakes.Database, tables, fakes.Written, new TableFiles(Microsoft.Extensions.Options.Options.Create(new Snail.MCP.Connector.Configuration.ConnectorOptions())), copy, TimeProvider.System, options);
    }

    /// <summary>A SQL database whose driver cannot carry a value of the result into .NET, as Npgsql with an interval of months.</summary>
    private sealed class ThrowingTables : ITables
    {
        public Task<Answer<RowPage>> QueryAsync(ReachableSource source, string select, RowLimit limit, CancellationToken cancellationToken) =>
            throw new InvalidCastException("Cannot read interval values with non-zero months as TimeSpan");

        public Task<Answer<IReadOnlyList<string>>> TablesAsync(ReachableSource source, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Answer<CollectionShape>> ShapeAsync(ReachableSource source, string table, CancellationToken cancellationToken) => throw new NotSupportedException();

        public IAsyncEnumerable<string> StreamAsync(ReachableSource source, string select, int most, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Answer<WritePreview>> PreviewAsync(ReachableSource source, string table, string filter, int sample, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Answer<long>> WriteAsync(ReachableSource source, WriteProposal proposal, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}

/// <summary>A real MongoDB holding what BSON allows and the JSON reader of the rows does not.</summary>
public sealed class MongoChaosTests(MongoFixture mongo) : IClassFixture<MongoFixture>, IDisposable
{
    private static readonly RowLimit Page = new(Rows: 10, Bytes: 65536, Seconds: 30);

    private readonly MongoDocuments _documents = new(Microsoft.Extensions.Options.Options.Create(new ConnectorOptions()));

    public void Dispose() => _documents.Dispose();

    /// <summary>
    /// BSON nests a hundred levels; <c>JsonNode.Parse</c>, which every row goes through, stops at sixty-four. A document
    /// nested seventy deep throws a JsonException the adapter does not catch.
    /// </summary>
    [Fact]
    public async Task FindAsync_DocumentNestedSeventyDeep_AnswersInsteadOfThrowing()
    {
        var deep = new BsonDocument("level", 70);
        for (int level = 69; level > 0; level--)
            deep = new BsonDocument { ["level"] = level, ["inner"] = deep };

        var source = mongo.Source with { Source = mongo.Source.Source with { Collections = [Source.Every] } };
        await new MongoClient(source.Access.ReadUri).GetDatabase("sales").GetCollection<BsonDocument>("deep").InsertOneAsync(deep);

        var thrown = await Record.ExceptionAsync(() => _documents.FindAsync(source, new DocumentQuery("deep", "{}", null, null, Page), CancellationToken.None));

        Assert.Null(thrown);
    }
}
