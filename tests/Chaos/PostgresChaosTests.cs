using System.Diagnostics;
using Npgsql;
using Snail.MCP.Connector.Adapters;
using Snail.MCP.Connector.Application;
using Snail.MCP.Connector.Application.Ports;
using Snail.MCP.Connector.Configuration;
using Snail.MCP.Connector.Tests.Adapters;

namespace Snail.MCP.Connector.Tests.Chaos;

/// <summary>
/// A real PostgreSQL behind the rules: what a statement the rules let through does there, values the driver cannot carry
/// into .NET, and a page cut after its first row of a large result.
/// </summary>
/// <remarks>
/// The fixture's user is the container's superuser, as in <see cref="SqlTablesTests"/>: these tests prove what the rules
/// and the transaction hold by themselves, without a read-only role behind them.
/// </remarks>
public sealed class PostgresChaosTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly RowLimit Page = new(Rows: 10, Bytes: 65536, Seconds: 30);

    private readonly SqlTables _tables = new(Microsoft.Extensions.Options.Options.Create(new ConnectorOptions()));

    private ReachableSource Orders => database.Source;

    private ReachableSource Everything => database.Source with { Source = database.Source.Source with { Collections = [Source.Every] } };

    public async Task InitializeAsync()
    {
        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(database.Source.Access.ReadUri) { Database = "ledger" }.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS secrets (word text PRIMARY KEY); INSERT INTO secrets VALUES ('the-secret') ON CONFLICT DO NOTHING";
        await command.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>A source that allows only <c>orders</c> answers the rows of <c>secrets</c>, through statements the rules accept.</summary>
    [Theory]
    [InlineData("""SELECT 1 AS "a'", * FROM secrets AS "b'" """)]
    [InlineData("""SELECT E'\'' AS a, * FROM secrets WHERE ''=''""")]
    [InlineData("SELECT table_to_xml('secrets', true, false, '')::text AS x")]
    public async Task QueryAsync_TableTheSourceKeepsOut_IsNotRead(string statement)
    {
        var checkedSelect = SqlRules.Select(statement, Orders.Source);
        if (checkedSelect.Value is not { } accepted)
            return;

        var page = await _tables.QueryAsync(Orders, accepted, Page, CancellationToken.None);

        Assert.DoesNotContain(page.Value?.Rows ?? [], row => row.Contains("the-secret", StringComparison.Ordinal));
    }

    /// <summary>
    /// "A transaction that is always rolled back, READ ONLY on PostgreSQL" — until the statement ends it. A COMMIT hidden
    /// behind an escape string closes the read-only transaction; the DELETE after it runs in a transaction of its own and
    /// is committed before the server's rollback comes. Only the database role is left standing.
    /// </summary>
    [Fact]
    public async Task QueryAsync_StatementThatCommitsItsOwnTransaction_ChangesNothing()
    {
        const string statement = """SELECT E'\''; COMMIT; DELETE FROM orders WHERE number = 1; SELECT ''''""";
        var checkedSelect = SqlRules.Select(statement, Orders.Source);

        if (checkedSelect.Value is { } accepted)
            await _tables.QueryAsync(Orders, accepted, Page, CancellationToken.None);

        var counted = await _tables.QueryAsync(Orders, "SELECT count(*) AS n FROM orders", Page, CancellationToken.None);
        Assert.Equal("""{"n":30}""", Assert.Single(counted.Value!.Rows));
    }

    /// <summary>
    /// Every call of the port "answers in data". <c>age()</c> — the first thing an analysis of overdue invoices writes —
    /// returns an interval of months, which Npgsql will not read as a TimeSpan; NaN and a number past decimal's range are
    /// no decimal. The exception is none the adapter catches, so the tool fails without an answer and without a journal line.
    /// </summary>
    [Theory]
    [InlineData("SELECT age(timestamp '2026-10-30', timestamp '2025-01-01') AS waited")]
    [InlineData("SELECT 'NaN'::numeric AS ratio")]
    [InlineData("SELECT 1e40::numeric AS big")]
    public async Task QueryAsync_ValueTheDriverCannotCarry_AnswersInsteadOfThrowing(string statement)
    {
        var thrown = await Record.ExceptionAsync(() => _tables.QueryAsync(Everything, statement, Page, CancellationToken.None));

        Assert.Null(thrown);
    }

    /// <summary>
    /// "Reading stops there rather than pulling the rest of a large result over the network." Closing a reader without
    /// cancelling its command makes Npgsql read the result to its end: a page of one row of a hundred million holds the
    /// connection — and the tool call — for as long as the whole result takes. The series stands in the SELECT list,
    /// where PostgreSQL streams it; in FROM it would be made whole before the first row.
    /// </summary>
    [Fact]
    public async Task QueryAsync_PageCutAfterOneRowOfAHugeResult_AnswersAtOnce()
    {
        var watch = Stopwatch.StartNew();

        var page = await _tables.QueryAsync(Everything, "SELECT generate_series(1, 100000000) AS g", Page with { Rows = 1 }, CancellationToken.None);

        watch.Stop();
        Assert.True(page.Value?.IsCut, page.Error);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"took {watch.Elapsed.TotalSeconds:0.0} s");
    }
}
