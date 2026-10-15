using Snail.MCP.Connector.Application;

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
    public void Select_TableTheSourceKeeps_IsRefusedNamingIt(string statement)
    {
        var checkedSelect = SqlRules.Select(statement, Ledger);

        Assert.Contains("salaries", checkedSelect.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Select_EveryTableAllowed_AcceptsAnyTable() =>
        Assert.NotNull(SqlRules.Select("SELECT * FROM hr.salaries", Ledger with { Collections = [Source.Every] }).Value);
}
