using System.Data.Common;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Npgsql;
using Snail.MCP.Connector.Adapters;
using Snail.MCP.Connector.Application;
using Snail.MCP.Connector.Application.Ports;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;

namespace Snail.MCP.Connector.Tests.Adapters;

/// <summary>A SQL database with the ledger the tests read: thirty orders, an index on the city.</summary>
public abstract class SqlFixture : IAsyncLifetime
{
    /// <summary>A source of the ledger as the connector reaches it.</summary>
    public ReachableSource Source { get; private set; } = default!;

    /// <summary>The same source with a writing user — the server's own, as the tests have no other.</summary>
    public ReachableSource Writable => Source with { Access = Source.Access with { WriteUri = Source.Access.ReadUri } };

    /// <summary>The engine.</summary>
    protected abstract SourceKind Kind { get; }

    /// <summary>The statements that make and fill the ledger, in the engine's own dialect.</summary>
    protected abstract IReadOnlyList<string> Seed { get; }

    public async Task InitializeAsync()
    {
        string connection = await StartAsync();
        Source = new ReachableSource(new Source("ledger", Kind, "ledger", ["orders"]), new SourceAccess(connection, null));

        await using var database = Opened(connection);
        await database.OpenAsync();
        foreach (string statement in Seed)
        {
            await using var command = database.CreateCommand();
            command.CommandText = statement;
            await command.ExecuteNonQueryAsync();
        }
    }

    public abstract Task DisposeAsync();

    /// <summary>Starts the server and makes the ledger database; the connection string of the server.</summary>
    protected abstract Task<string> StartAsync();

    /// <summary>A connection to the ledger database.</summary>
    protected abstract DbConnection Opened(string connection);

    /// <summary>The thirty orders as one INSERT.</summary>
    protected static string Orders(Func<int, string> paid, Func<int, string> at) =>
        "INSERT INTO orders (number, city, total, paid, at) VALUES "
        + string.Join(", ", Enumerable.Range(1, 30).Select(number => string.Create(CultureInfo.InvariantCulture, $"({number}, '{(number % 2 == 0 ? "Madrid" : "Valencia")}', {number * 10.5m:0.00}, {paid(number)}, {at(number)})")));
}

/// <summary>A real PostgreSQL, started once for the class.</summary>
public sealed class PostgresFixture : SqlFixture
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").WithDatabase("ledger").Build();

    protected override SourceKind Kind => SourceKind.PostgreSql;

    protected override IReadOnlyList<string> Seed =>
    [
        "CREATE TABLE orders (number int PRIMARY KEY, city text NOT NULL, total numeric(10, 2) NOT NULL, paid boolean NOT NULL, at timestamptz NOT NULL, note bytea)",
        "CREATE INDEX orders_city ON orders (city)",
        Orders(number => number % 3 == 0 ? "true" : "false", number => $"'2026-10-{number % 28 + 1:00}T00:00:00Z'"),
        "ANALYZE orders"
    ];

    public override Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    protected override async Task<string> StartAsync()
    {
        await _postgres.StartAsync();

        return _postgres.GetConnectionString();
    }

    protected override DbConnection Opened(string connection) => new NpgsqlConnection(connection);
}

/// <summary>A real SQL Server, started once for the class.</summary>
public sealed class SqlServerFixture : SqlFixture
{
    private readonly MsSqlContainer _sqlServer = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    protected override SourceKind Kind => SourceKind.SqlServer;

    protected override IReadOnlyList<string> Seed =>
    [
        "CREATE TABLE orders (number int PRIMARY KEY, city nvarchar(40) NOT NULL, total decimal(10, 2) NOT NULL, paid bit NOT NULL, at datetime2 NOT NULL, note varbinary(max))",
        "CREATE INDEX orders_city ON orders (city)",
        Orders(number => number % 3 == 0 ? "1" : "0", number => $"'2026-10-{number % 28 + 1:00}T00:00:00'")
    ];

    public override Task DisposeAsync() => _sqlServer.DisposeAsync().AsTask();

    protected override async Task<string> StartAsync()
    {
        await _sqlServer.StartAsync();
        string server = _sqlServer.GetConnectionString();

        await using var master = new SqlConnection(server);
        await master.OpenAsync();
        await using var create = master.CreateCommand();
        create.CommandText = "CREATE DATABASE ledger";
        await create.ExecuteNonQueryAsync();

        return server;
    }

    protected override DbConnection Opened(string connection) =>
        new SqlConnection(new SqlConnectionStringBuilder(connection) { InitialCatalog = "ledger" }.ConnectionString);
}

/// <summary>A SQL database read as the connector reads it: its tables, a table's shape, pages cut by rows, nothing written, slow queries stopped.</summary>
public abstract class SqlTablesTests(SqlFixture database)
{
    private static readonly RowLimit Page = new(Rows: 10, Bytes: 65536, Seconds: 30);

    private readonly SqlTables _tables = new();

    /// <summary>A statement that waits five seconds, in the engine's dialect.</summary>
    protected abstract string Slow { get; }

    private static WriteProposal Proposal(WriteOperation operation, string filter, string change, long matched) => new()
    {
        Id = "w1",
        Task = "t",
        Source = "ledger",
        Collection = "orders",
        Operation = operation,
        Filter = filter,
        Change = change,
        Reason = "test",
        Matched = matched,
        At = DateTimeOffset.UnixEpoch
    };

    private async Task<long> CountAsync(string select)
    {
        var page = (await _tables.QueryAsync(database.Source, select, Page, CancellationToken.None)).Value!;

        return System.Text.Json.Nodes.JsonNode.Parse(page.Rows[0])!["n"]!.GetValue<long>();
    }

    [Fact]
    public async Task TablesAsync_Ledger_ListsOrdersWithItsSchema()
    {
        var tables = (await _tables.TablesAsync(database.Source, CancellationToken.None)).Value!;

        Assert.Contains(tables, table => table.EndsWith(".orders", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ShapeAsync_Orders_NamesColumnsTypesExamplesAndIndexes()
    {
        var shape = (await _tables.ShapeAsync(database.Source, "orders", CancellationToken.None)).Value!;

        Assert.Equal(["number", "city", "total", "paid", "at", "note"], shape.Fields.Select(field => field.Path));
        Assert.Contains(shape.Fields, field => field.Path == "city" && field.Share == 1 && field.Example is not null);
        Assert.Contains(shape.Fields, field => field.Path == "note" && field.Example is null);
        Assert.Contains(shape.Indexes, index => index.Contains("orders_city", StringComparison.Ordinal));
    }

    [Fact]
    public async Task QueryAsync_MoreThanTheLimit_IsCutAndSaysSo()
    {
        var page = (await _tables.QueryAsync(database.Source, "SELECT number FROM orders ORDER BY number", Page, CancellationToken.None)).Value!;

        Assert.Equal(10, page.Rows.Count);
        Assert.True(page.IsCut);
        Assert.Equal("""{"number":1}""", page.Rows[0]);
    }

    [Fact]
    public async Task QueryAsync_Grouped_ReadsNumbersAsNumbers()
    {
        var page = (await _tables.QueryAsync(database.Source, "SELECT city, COUNT(*) AS orders, SUM(total) AS total FROM orders GROUP BY city ORDER BY city", Page, CancellationToken.None)).Value!;

        Assert.False(page.IsCut);
        Assert.Equal("""{"city":"Madrid","orders":15,"total":2520.00}""", page.Rows[0]);
    }

    /// <summary>Whatever ran, the transaction is rolled back: a write sent past the rules does not stay.</summary>
    [Fact]
    public async Task QueryAsync_WriteSentPastTheRules_LeavesNoTrace()
    {
        await _tables.QueryAsync(database.Source, "DELETE FROM orders", Page, CancellationToken.None);

        var counted = (await _tables.QueryAsync(database.Source, "SELECT COUNT(*) AS n FROM orders", Page, CancellationToken.None)).Value!;
        Assert.Equal("""{"n":30}""", counted.Rows[0]);
    }

    [Fact]
    public async Task QueryAsync_LongerThanItsSeconds_IsStoppedAsTooSlow()
    {
        var page = await _tables.QueryAsync(database.Source, Slow, Page with { Seconds = 1 }, CancellationToken.None);

        Assert.Equal(Messages.TooSlow(1), page.Error);
    }

    [Fact]
    public async Task QueryAsync_UnknownTable_SaysWhatTheDatabaseAnswered()
    {
        var page = await _tables.QueryAsync(database.Source, "SELECT * FROM salaries", Page, CancellationToken.None);

        Assert.StartsWith(Messages.DatabaseSaid("ledger", string.Empty), page.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("Password=", page.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PreviewAsync_ColumnsAndComparisons_CountsAndShowsTheRows()
    {
        var preview = (await _tables.PreviewAsync(database.Source, "orders", """{"city": "Madrid", "number": {"$lte": 10}}""", 3, CancellationToken.None)).Value!;

        Assert.Equal(5, preview.Matched);
        Assert.Equal(3, preview.Sample.Count);
    }

    [Fact]
    public async Task PreviewAsync_DateAsText_IsReadAsTheColumnsType()
    {
        var preview = (await _tables.PreviewAsync(database.Source, "orders", """{"at": {"$gte": "2026-10-20"}}""", 3, CancellationToken.None)).Value!;

        Assert.Equal(9, preview.Matched);
    }

    [Fact]
    public async Task WriteAsync_UpdateOfWhatWasMatched_IsCommitted()
    {
        var written = await _tables.WriteAsync(database.Writable, Proposal(WriteOperation.UpdateOne, """{"number": 1}""", """{"$set": {"city": "Lugo"}, "$inc": {"total": 1}}""", 1), CancellationToken.None);
        long lugo = await CountAsync("SELECT COUNT(*) AS n FROM orders WHERE city = 'Lugo' AND total = 11.50");
        await _tables.WriteAsync(database.Writable, Proposal(WriteOperation.Update, """{"number": 1}""", """{"$set": {"city": "Valencia"}, "$inc": {"total": -1}}""", 1), CancellationToken.None);

        Assert.Equal(1, written.Value);
        Assert.Equal(1, lugo);
    }

    /// <summary>The data grew past what the person read: nothing is written.</summary>
    [Fact]
    public async Task WriteAsync_MoreRowsThanMatched_IsRolledBack()
    {
        var written = await _tables.WriteAsync(database.Writable, Proposal(WriteOperation.Update, """{"city": "Madrid"}""", """{"$set": {"city": "Zaragoza"}}""", 1), CancellationToken.None);

        Assert.Equal(Messages.Changed(15, 1), written.Error);
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) AS n FROM orders WHERE city = 'Zaragoza'"));
    }

    [Fact]
    public async Task WriteAsync_InsertThenDelete_AddsAndRemovesTheRow()
    {
        var inserted = await _tables.WriteAsync(database.Writable, Proposal(WriteOperation.Insert, "{}", """[{"number": 31, "city": "Lugo", "total": 1.5, "paid": true, "at": "2026-10-31T00:00:00"}]""", 1), CancellationToken.None);
        long added = await CountAsync("SELECT COUNT(*) AS n FROM orders WHERE number = 31");
        var deleted = await _tables.WriteAsync(database.Writable, Proposal(WriteOperation.Delete, """{"number": 31}""", "{}", 1), CancellationToken.None);

        Assert.Equal(1, inserted.Value);
        Assert.Equal(1, added);
        Assert.Equal(1, deleted.Value);
        Assert.Equal(30, await CountAsync("SELECT COUNT(*) AS n FROM orders"));
    }

    [Fact]
    public async Task WriteAsync_SourceWithoutAWriter_IsRefused()
    {
        var written = await _tables.WriteAsync(database.Source, Proposal(WriteOperation.Delete, """{"number": 1}""", "{}", 1), CancellationToken.None);

        Assert.Equal(Messages.NoWriter, written.Error);
    }

    [Fact]
    public async Task StreamAsync_Most_ReadsOneMoreSoTheCallerKnowsItIsCut()
    {
        List<string> rows = [];
        await foreach (string row in _tables.StreamAsync(database.Source, "SELECT number FROM orders", 5, CancellationToken.None))
            rows.Add(row);

        Assert.Equal(6, rows.Count);
    }
}

/// <summary>The checks against PostgreSQL, whose transaction also refuses a write itself.</summary>
public sealed class PostgresTablesTests(PostgresFixture postgres) : SqlTablesTests(postgres), IClassFixture<PostgresFixture>
{
    private readonly SqlTables _tables = new();

    protected override string Slow => "SELECT pg_sleep(5)";

    [Fact]
    public async Task QueryAsync_Write_IsRefusedByTheReadOnlyTransaction()
    {
        var page = await _tables.QueryAsync(postgres.Source, "UPDATE orders SET city = 'Lugo'", new RowLimit(10, 65536, 30), CancellationToken.None);

        Assert.Contains("read-only", page.Error, StringComparison.Ordinal);
    }
}

/// <summary>The checks against SQL Server.</summary>
public sealed class SqlServerTablesTests(SqlServerFixture sqlServer) : SqlTablesTests(sqlServer), IClassFixture<SqlServerFixture>
{
    protected override string Slow => "WAITFOR DELAY '00:00:05'; SELECT 1 AS n";
}
