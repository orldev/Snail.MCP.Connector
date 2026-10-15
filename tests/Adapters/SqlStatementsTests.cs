using System.Text.Json.Nodes;
using Snail.MCP.Connector.Adapters;

namespace Snail.MCP.Connector.Tests.Adapters;

/// <summary>The statements the server writes for a SQL proposal: names quoted, every value a parameter.</summary>
public sealed class SqlStatementsTests
{
    private static WriteProposal Proposal(WriteOperation operation, string filter, string change) => new()
    {
        Id = "w1",
        Task = "t",
        Source = "erp",
        Collection = "invoices",
        Operation = operation,
        Filter = filter,
        Change = change,
        Reason = "r",
        Matched = 1,
        At = DateTimeOffset.UnixEpoch
    };

    [Fact]
    public void Write_UpdateOnPostgres_SetsAndIncrementsByParameters()
    {
        var statement = Assert.Single(SqlStatements.Write(SourceKind.PostgreSql, "\"public\".\"invoices\"",
            Proposal(WriteOperation.Update, """{"id": 7, "due": {"$lt": "2026-06-01"}, "note": null}""", """{"$set": {"status": "paid"}, "$inc": {"paid_count": 1}}""")));

        Assert.Equal("""UPDATE "public"."invoices" SET "status" = @p0, "paid_count" = "paid_count" + @p1 WHERE "id" = @p2 AND "due" < @p3 AND "note" IS NULL""", statement.Text);
        Assert.Equal(["\"paid\"", "1", "7", "\"2026-06-01\""], statement.Values.Select(value => value!.ToJsonString()));
    }

    [Fact]
    public void Write_DeleteOnSqlServer_QuotesWithBracketsAndListsIn()
    {
        var statement = Assert.Single(SqlStatements.Write(SourceKind.SqlServer, "[dbo].[invoices]",
            Proposal(WriteOperation.Delete, """{"status": {"$in": ["void", "draft"]}, "paid_at": {"$null": false}}""", "{}")));

        Assert.Equal("DELETE FROM [dbo].[invoices] WHERE [status] IN (@p0, @p1) AND [paid_at] IS NOT NULL", statement.Text);
    }

    [Fact]
    public void Write_Insert_OneStatementARow()
    {
        var statements = SqlStatements.Write(SourceKind.PostgreSql, "\"public\".\"payments\"",
            Proposal(WriteOperation.Insert, "{}", """[{"id": 1, "amount": 10.5}, {"id": 2, "amount": null}]"""));

        Assert.Equal(2, statements.Count);
        Assert.Equal("""INSERT INTO "public"."payments" ("id", "amount") VALUES (@p0, @p1)""", statements[0].Text);
        Assert.Null(statements[1].Values[1]);
    }

    [Fact]
    public void Count_Filter_CountsWhatTheWriteWouldTouch()
    {
        var statement = SqlStatements.Count(SourceKind.SqlServer, "[dbo].[invoices]", JsonNode.Parse("""{"id": {"$gte": 5, "$lte": 9}}""")!.AsObject());

        Assert.Equal("SELECT COUNT(*) FROM [dbo].[invoices] WHERE [id] >= @p0 AND [id] <= @p1", statement.Text);
    }
}
