using MongoDB.Bson;
using MongoDB.Driver;
using Snail.MCP.Connector.Adapters;
using Snail.MCP.Connector.Application;
using Snail.MCP.Connector.Application.Ports;
using Testcontainers.MongoDb;

namespace Snail.MCP.Connector.Tests.Adapters;

/// <summary>A real MongoDB, started once for the class.</summary>
/// <remarks>
/// MongoDB 7: 8.0 and later stop themselves on a Linux kernel of 6.19 or newer (tcmalloc), and Docker Desktop's own VM
/// ships such a kernel — the production image is newer, but what these tests prove does not depend on the version.
/// </remarks>
public sealed class MongoFixture : IAsyncLifetime
{
    private readonly MongoDbContainer _mongo = new MongoDbBuilder("mongo:7.0").Build();

    /// <summary>A source of the database the tests read.</summary>
    public ReachableSource Source { get; private set; } = default!;

    public async Task InitializeAsync()
    {
        await _mongo.StartAsync();
        Source = new ReachableSource(new Source("sales", SourceKind.MongoDb, "sales", ["orders"]), new SourceAccess(_mongo.GetConnectionString(), null));

        var orders = new MongoClient(_mongo.GetConnectionString()).GetDatabase("sales").GetCollection<BsonDocument>("orders");
        await orders.InsertManyAsync(Enumerable.Range(1, 30).Select(Order));
        await orders.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("status")));
    }

    public Task DisposeAsync() => _mongo.DisposeAsync().AsTask();

    private static BsonDocument Order(int number) => new()
    {
        ["number"] = number,
        ["status"] = number % 3 == 0 ? "paid" : "new",
        ["total"] = number * 10.5,
        ["customer"] = new BsonDocument { ["name"] = $"Customer {number % 4}", ["city"] = "Madrid" },
        ["lines"] = new BsonArray { new BsonDocument { ["sku"] = "SNAIL", ["qty"] = number } },
        ["at"] = new DateTime(2026, 10, number % 28 + 1, 0, 0, 0, DateTimeKind.Utc)
    };
}

/// <summary>MongoDB read as the connector reads it: shape from a sample, pages cut by rows and bytes, pipelines, counts and streams.</summary>
public sealed class MongoDocumentsTests(MongoFixture mongo) : IClassFixture<MongoFixture>, IDisposable
{
    private static readonly RowLimit Page = new(Rows: 10, Bytes: 65536, Seconds: 30);

    private readonly MongoDocuments _documents = new();

    public void Dispose() => _documents.Dispose();

    [Fact]
    public async Task ShapeAsync_Orders_NamesNestedFieldsTypesAndIndexes()
    {
        var shape = (await _documents.ShapeAsync(mongo.Source, "orders", 200, CancellationToken.None)).Value!;

        Assert.Equal(30, shape.Documents);
        Assert.Contains(shape.Fields, field => field.Path == "customer.city" && field.Types.SequenceEqual(["string"]) && field.Share == 1);
        Assert.Contains(shape.Fields, field => field.Path == "lines[].qty" && field.Types.Contains("int"));
        Assert.Contains(shape.Fields, field => field.Path == "at" && field.Types.SequenceEqual(["date"]));
        Assert.Contains(shape.Indexes, index => index.Contains("status", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FindAsync_MoreThanTheLimit_IsCutAndSaysSo()
    {
        var found = (await _documents.FindAsync(mongo.Source, new DocumentQuery("orders", "{}", null, """{"number": 1}""", Page), CancellationToken.None)).Value!;

        Assert.Equal(10, found.Rows.Count);
        Assert.True(found.IsCut);
        Assert.Contains("\"number\":1", found.Rows[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindAsync_FilterAndProjection_ReadsOnlyThoseFields()
    {
        var found = (await _documents.FindAsync(mongo.Source, new DocumentQuery("orders", """{"status": "paid"}""", """{"number": 1, "_id": 0}""", null, Page), CancellationToken.None)).Value!;

        Assert.Equal(10, found.Rows.Count);
        Assert.False(found.IsCut);
        Assert.All(found.Rows, row => Assert.DoesNotContain("customer", row, StringComparison.Ordinal));
    }

    /// <summary>Wide documents fill a model's window before the row limit does: the bytes cut the page first.</summary>
    [Fact]
    public async Task FindAsync_BytesBelowOneRow_IsCutBeforeTheRowLimit()
    {
        var found = (await _documents.FindAsync(mongo.Source, new DocumentQuery("orders", "{}", null, null, Page with { Bytes = 100 }), CancellationToken.None)).Value!;

        Assert.Empty(found.Rows);
        Assert.True(found.IsCut);
    }

    [Fact]
    public async Task AggregateAsync_GroupByStatus_SumsTheTotals()
    {
        const string pipeline = """[{"$group": {"_id": "$status", "orders": {"$sum": 1}}}, {"$sort": {"_id": 1}}]""";

        var grouped = (await _documents.AggregateAsync(mongo.Source, "orders", pipeline, Page, CancellationToken.None)).Value!;

        Assert.Equal(2, grouped.Rows.Count);
        Assert.Contains("\"orders\":20", grouped.Rows[0], StringComparison.Ordinal);
    }

    /// <summary>A sum of prices a person reads: written the shortest way, not with the driver's seventeen digits.</summary>
    [Fact]
    public void Row_FractionWithALongTail_IsWrittenTheShortestWay()
    {
        var document = new BsonDocument { ["revenue"] = 7533.7, ["orders"] = 52, ["at"] = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc) };

        string row = MongoDocuments.Row(document);

        Assert.Equal("""{"revenue":7533.7,"orders":52,"at":{"$date":"2026-10-01T00:00:00Z"}}""", row);
    }

    [Fact]
    public async Task CountAsync_Filter_CountsTheMatches()
    {
        var counted = await _documents.CountAsync(mongo.Source, "orders", """{"status": "paid"}""", CancellationToken.None);

        Assert.Equal(10, counted.Value);
    }

    [Fact]
    public async Task StreamAsync_Pipeline_YieldsUpToOnePastTheMost()
    {
        int rows = 0;
        await foreach (string row in _documents.StreamAsync(mongo.Source, new DocumentQuery("orders", "{}", null, null, Page), """[{"$match": {}}]""", 5, CancellationToken.None))
            rows++;

        Assert.Equal(6, rows);
    }

    [Fact]
    public async Task PreviewAsync_Filter_CountsAndShowsAFew()
    {
        var preview = (await _documents.PreviewAsync(mongo.Source, "orders", """{"status": "paid"}""", 3, CancellationToken.None)).Value!;

        Assert.Equal(10, preview.Matched);
        Assert.Equal(3, preview.Sample.Count);
    }

    /// <summary>An allowed update runs by the writing user and changes exactly the matched documents.</summary>
    [Fact]
    public async Task WriteAsync_AllowedUpdate_ChangesTheMatchedDocuments()
    {
        var writable = mongo.Source with { Source = mongo.Source.Source with { Collections = [Source.Every], CanWrite = true }, Access = mongo.Source.Access with { WriteUri = mongo.Source.Access.ReadUri } };
        var deals = new MongoClient(writable.Access.ReadUri).GetDatabase("sales").GetCollection<BsonDocument>("deals");
        await deals.InsertManyAsync([new BsonDocument("stage", "lead"), new BsonDocument("stage", "lead"), new BsonDocument("stage", "lost")]);
        var proposal = Proposal("deals", WriteOperation.Update, """{"stage": "lead"}""", """{"$set": {"stage": "won"}}""");

        var written = await _documents.WriteAsync(writable, proposal, CancellationToken.None);

        Assert.Equal(2, written.Value);
        Assert.Equal(2, await deals.CountDocumentsAsync(new BsonDocument("stage", "won")));
        Assert.Equal(1, await deals.CountDocumentsAsync(new BsonDocument("stage", "lost")));
    }

    [Fact]
    public async Task WriteAsync_SourceWithoutAWriter_WritesNothing()
    {
        var proposal = Proposal("orders", WriteOperation.Delete, """{"number": 1}""", "{}");

        var written = await _documents.WriteAsync(mongo.Source, proposal, CancellationToken.None);

        Assert.Equal(Messages.NoWriter, written.Error);
    }

    /// <summary>A refused login says so in words, and the password in the connection string stays out of them.</summary>
    [Fact]
    public async Task CountAsync_WrongPassword_AnswersWithoutThePassword()
    {
        var builder = new MongoUrlBuilder(mongo.Source.Access.ReadUri) { Password = "not-the-password" };
        var wrong = mongo.Source with { Access = new SourceAccess(builder.ToString(), null) };

        var counted = await _documents.CountAsync(wrong, "orders", "{}", CancellationToken.None);

        Assert.StartsWith(Messages.DatabaseSaid("sales", string.Empty), counted.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("not-the-password", counted.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindAsync_FilterThatIsNotJson_AnswersInWords()
    {
        var found = await _documents.FindAsync(mongo.Source, new DocumentQuery("orders", "{status: ", null, null, Page), CancellationToken.None);

        Assert.Null(found.Value);
        Assert.NotNull(found.Error);
    }

    private static WriteProposal Proposal(string collection, WriteOperation operation, string filter, string change) => new()
    {
        Id = "w0000000000000001",
        Task = "task",
        Source = "sales",
        Collection = collection,
        Operation = operation,
        Filter = filter,
        Change = change,
        Reason = "a test",
        Matched = 1,
        At = DateTimeOffset.UtcNow
    };
}
