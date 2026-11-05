using Snail.MCP.Connector.Application;

namespace Snail.MCP.Connector.Tests.Chaos;

/// <summary>
/// The rules in front of the databases, fed what a model can write: quotes inside identifiers, PostgreSQL's escape
/// strings, functions that take a table by its name, a key written twice.
/// </summary>
public sealed class RulesChaosTests
{
    private static readonly Source Ledger = new("ledger", SourceKind.PostgreSql, "ledger", ["orders"]);

    private static readonly Source Accounts = new("accounts", SourceKind.SqlServer, "accounts", ["orders"]);

    private static readonly Source Sales = new("sales", SourceKind.MongoDb, "sales", ["orders", "customers"]);

    /// <summary>
    /// <c>Bare</c> takes every <c>'</c> for the start of a string literal, wherever it stands. A quote inside a quoted
    /// identifier — <c>[a']</c> on SQL Server, <c>"a'"</c> on PostgreSQL — or after a backslash in an <c>E'…'</c>
    /// string opens a "literal" the database never sees, and everything up to the next quote, the FROM included, is
    /// hidden from the check of the tables. A function that reads a table named in a string is not after FROM at all.
    /// </summary>
    [Theory]
    [InlineData("""SELECT 1 AS "a'", * FROM secrets AS "b'" """)]
    [InlineData("""SELECT E'\'' AS a, * FROM secrets WHERE ''=''""")]
    [InlineData("SELECT table_to_xml('secrets', true, false, '')")]
    [InlineData("SELECT query_to_xml('select * from secrets', true, false, '')")]
    public void Select_TableTheSourceKeepsOut_IsRefused(string statement)
    {
        var checkedSelect = SqlRules.Select(statement, Ledger);

        Assert.Null(checkedSelect.Value);
    }

    /// <summary>The same hiding takes the forbidden words and the second statement out of sight: one SELECT becomes three statements.</summary>
    [Theory]
    [InlineData("""SELECT E'\''; COMMIT; DELETE FROM orders; SELECT ''''""")]
    public void Select_SecondStatementBehindAQuote_IsRefused(string statement)
    {
        var checkedSelect = SqlRules.Select(statement, Ledger);

        Assert.Null(checkedSelect.Value);
    }

    /// <summary>On SQL Server a bracketed name hides the same way, and no READ ONLY transaction stands behind the rules there.</summary>
    [Theory]
    [InlineData("SELECT 1 AS [a'], * FROM secrets AS [b']")]
    [InlineData("SELECT 1 AS [a']; DELETE FROM orders; SELECT 1 AS [b']")]
    public void Select_QuoteInsideABracketedNameOnSqlServer_IsRefused(string statement)
    {
        var checkedSelect = SqlRules.Select(statement, Accounts);

        Assert.Null(checkedSelect.Value);
    }

    /// <summary>
    /// A key written twice in one JSON object. Whatever the parser keeps, the rules must not throw — an exception here is
    /// no answer and no journal line — and what goes on to MongoDB must name only collections the source allows.
    /// </summary>
    [Fact]
    public void Pipeline_LookupWithItsFromWrittenTwice_IsRefusedOrNamesOnlyAllowed()
    {
        const string pipeline = """[{"$lookup": {"from": "customers", "from": "salaries", "localField": "c", "foreignField": "_id", "as": "who"}}]""";

        Answer<System.Text.Json.Nodes.JsonArray>? checkedPipeline = null;
        var thrown = Record.Exception(() => checkedPipeline = QueryRules.Pipeline(pipeline, Sales));

        Assert.Null(thrown);
        Assert.True(checkedPipeline!.Value is null || !checkedPipeline.Value.ToJsonString().Contains("salaries", StringComparison.Ordinal));
    }
}
