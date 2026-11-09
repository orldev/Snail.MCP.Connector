using Snail.MCP.Connector.Application;
using Snail.Sdk.Mcp.Hosting;

namespace Snail.MCP.Connector.Tests.Application;

/// <summary>What a SQL statement may be: one SELECT reading allowed tables; a word hidden in a string or a comment neither refuses nor lets in.</summary>
public sealed class SqlRulesTests
{
    private static readonly Source Ledger = new("ledger", SourceKind.PostgreSql, "ledger", ["orders", "customers"]);

    [Theory]
    [InlineData("SELECT city, count(*) FROM customers GROUP BY city")]
    [InlineData("select o.total from public.orders o join customers c on c.id = o.customer_id;")]
    [InlineData("WITH paid AS (SELECT * FROM orders WHERE status = 'paid') SELECT count(*) FROM paid")]
    [InlineData("SELECT 'drop table orders' AS said, 1 -- delete everything\nFROM orders")]
    [InlineData("SELECT * FROM \"orders\" /* insert */")]
    public void Select_ReadOfAllowedTables_IsAccepted(string statement) =>
        Assert.NotNull(SqlRules.Select(statement, Ledger).Value);

    /// <summary>The quoting each engine reads stays legal: what the rules learned to read is not refused for being there.</summary>
    [Theory]
    [InlineData(SourceKind.PostgreSql, """SELECT "Total Due", note FROM orders""")]
    [InlineData(SourceKind.PostgreSql, """SELECT E'it\'s' AS said, total FROM orders""")]
    [InlineData(SourceKind.PostgreSql, "SELECT $$a 'quoted' word$$ AS said FROM orders")]
    [InlineData(SourceKind.PostgreSql, "SELECT $tag$x$tag$ AS said FROM orders")]
    [InlineData(SourceKind.PostgreSql, "SELECT data['city'] FROM orders WHERE total > $1")]
    [InlineData(SourceKind.SqlServer, "SELECT [Total Due], [it's] FROM orders")]
    [InlineData(SourceKind.SqlServer, "SELECT N'Ñandú' AS said FROM [dbo].[orders]")]
    public void Select_QuotingOfTheEngine_IsAccepted(SourceKind kind, string statement) =>
        Assert.NotNull(SqlRules.Select(statement, Ledger with { Kind = kind }).Value);

    [Theory]
    [InlineData("INSERT INTO orders VALUES (1)", null)]
    [InlineData("SELECT * INTO copy FROM orders", "INTO")]
    [InlineData("WITH gone AS (DELETE FROM orders RETURNING *) SELECT * FROM gone", "DELETE")]
    [InlineData("SELECT 1; EXEC xp_cmdshell 'dir'", null)]
    [InlineData("DROP TABLE orders", null)]
    public void Select_StatementThatWrites_IsRefused(string statement, string? word)
    {
        var checkedSelect = SqlRules.Select(statement, Ledger);

        Assert.Null(checkedSelect.Value);
        Assert.Equal(word is null ? ToolFailures.Arguments : "notAllowed", checkedSelect.Type);
        if (word is not null)
            Assert.Equal(Messages.SqlForbidden(word), checkedSelect.Error);
    }

    [Fact]
    public void Select_TwoStatements_IsRefusedAsMoreThanOne() =>
        Assert.Equal(Messages.OneStatement, SqlRules.Select("SELECT 1 FROM orders; SELECT 2 FROM orders", Ledger).Error);

    /// <summary>A table the source keeps out is refused by name, joined or not.</summary>
    [Theory]
    [InlineData("SELECT * FROM salaries")]
    [InlineData("SELECT * FROM orders o JOIN hr.salaries s ON s.id = o.id")]
    [InlineData("SELECT * FROM orders o, salaries s")]
    [InlineData("SELECT * FROM orders AS o, customers c, hr.salaries")]
    [InlineData("SELECT * FROM (SELECT 1 AS n) t, salaries")]
    [InlineData("SELECT * FROM orders o CROSS APPLY salaries s")]
    [InlineData("SELECT * FROM orders o, LATERAL (SELECT * FROM salaries) s")]
    [InlineData("SELECT * FROM orders WHERE id IN (SELECT id FROM salaries)")]
    [InlineData("SELECT * FROM salaries_by_month()")]
    public void Select_TableTheSourceKeeps_IsRefusedNamingIt(string statement)
    {
        var checkedSelect = SqlRules.Select(statement, Ledger);

        Assert.Contains("salaries", checkedSelect.Error, StringComparison.Ordinal);
        Assert.Equal("notAllowed", checkedSelect.Type);
    }

    [Fact]
    public void Select_EveryTableAllowed_AcceptsAnyTable() =>
        Assert.NotNull(SqlRules.Select("SELECT * FROM hr.salaries", Ledger with { Collections = [Source.Every] }).Value);

    [Fact]
    public void Read_JoinWithCte_NamesTheTablesOnceAndNotTheCte() =>
        Assert.Equal(["public.orders", "customers"], SqlRules.Read("WITH paid AS (SELECT * FROM public.orders) SELECT * FROM paid JOIN customers c ON true JOIN public.orders o ON true", SourceKind.PostgreSql));

    [Theory]
    [InlineData("""{"id": 7}""")]
    [InlineData("""{"status": "open", "due": {"$lt": "2026-06-01", "$gte": "2026-01-01"}}""")]
    [InlineData("""{"city": {"$in": ["Madrid", "Bilbao"]}, "paid_at": {"$null": true}}""")]
    public void WriteFilter_ColumnsAndComparisons_IsAccepted(string filter) =>
        Assert.NotNull(SqlWriteRules.Filter(filter).Value);

    [Theory]
    [InlineData("{}")]
    [InlineData("[1]")]
    [InlineData("""{"id": {"$regex": "7"}}""")]
    [InlineData("""{"id": {"$in": []}}""")]
    [InlineData("""{"id": {"$null": "yes"}}""")]
    [InlineData("""{"id) OR (1=1": 7}""")]
    [InlineData("""{"id": [7]}""")]
    public void WriteFilter_AnythingElse_IsRefused(string filter) =>
        Assert.Equal(ToolFailures.Arguments, SqlWriteRules.Filter(filter).Type);

    [Theory]
    [InlineData("""{"$set": {"status": "paid", "note": null}}""", true)]
    [InlineData("""{"$inc": {"stock": -1}}""", true)]
    [InlineData("""{"$inc": {"stock": "1"}}""", false)]
    [InlineData("""{"status": "paid"}""", false)]
    [InlineData("""{"$set": {}}""", false)]
    public void WriteUpdate_SetAndIncOnly(string update, bool isAccepted) =>
        Assert.Equal(isAccepted, SqlWriteRules.Update(update).Value is not null);

    [Theory]
    [InlineData("""[{"id": 1, "amount": 10.5}]""", true)]
    [InlineData("""[{"id": 1, "lines": [1, 2]}]""", false)]
    [InlineData("""[{"bad name": 1}]""", false)]
    public void WriteRows_PlainColumnsOnly(string rows, bool isAccepted) =>
        Assert.Equal(isAccepted, SqlWriteRules.Rows(rows, 1000).Value is not null);

    [Theory]
    [InlineData("invoices", true)]
    [InlineData("public.invoices", true)]
    [InlineData("a.b.c", false)]
    [InlineData("invoices;", false)]
    [InlineData("\"invoices\"", false)]
    public void IsTable_OneOrTwoPlainNames(string table, bool isTable) =>
        Assert.Equal(isTable, SqlWriteRules.IsTable(table));

    /// <summary>FROM inside EXTRACT, SUBSTRING or TRIM names no table: such a query reads only what it reads.</summary>
    [Theory]
    [InlineData("SELECT EXTRACT(year FROM o.issued) AS y, count(*) FROM orders o GROUP BY 1")]
    [InlineData("SELECT SUBSTRING(name FROM 1 FOR 3) FROM customers")]
    [InlineData("SELECT TRIM(BOTH ' ' FROM name) FROM customers")]
    public void Select_FunctionThatSpellsFrom_IsAccepted(string statement) =>
        Assert.NotNull(SqlRules.Select(statement, Ledger).Value);

    [Fact]
    public void Read_FromList_NamesEveryTable() =>
        Assert.Equal(["orders", "customers"], SqlRules.Read("SELECT * FROM orders o, customers AS c WHERE o.c = c.id", SourceKind.PostgreSql));

    /// <summary>SQL Server keeps a table's case as written; the statement and the allowed list may differ in it.</summary>
    [Theory]
    [InlineData("SELECT * FROM Orders")]
    [InlineData("SELECT * FROM dbo.ORDERS")]
    [InlineData("SELECT * FROM [Orders]")]
    public void Select_TableInAnotherCase_IsAccepted(string statement) =>
        Assert.NotNull(SqlRules.Select(statement, Ledger with { Collections = ["Orders"] }).Value);
}
