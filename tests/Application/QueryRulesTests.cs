using Snail.MCP.Connector.Application;
using Snail.Sdk.Mcp.Hosting;

namespace Snail.MCP.Connector.Tests.Application;

/// <summary>What a query may ask: read only, and only the collections the source allows, joins included.</summary>
public sealed class QueryRulesTests
{
    private static readonly Source Sales = new("sales", SourceKind.MongoDb, "sales", ["orders", "customers"]);

    [Fact]
    public void Document_Empty_IsEveryDocument()
    {
        var parsed = QueryRules.Document(null, "filter");

        Assert.Empty(parsed.Value!);
    }

    [Fact]
    public void Document_NotAnObject_IsRefusedWithHowToWriteIt()
    {
        var parsed = QueryRules.Document("[1, 2]", "filter");

        Assert.Null(parsed.Value);
        Assert.Equal(Messages.NotAnObject("filter"), parsed.Error);
        Assert.Equal(ToolFailures.Arguments, parsed.Type);
        Assert.Equal(Messages.ExtendedJsonHint, parsed.Hint);
    }

    /// <summary>$where runs JavaScript inside the database, however deep in the filter it hides.</summary>
    [Fact]
    public void Document_WhereDeepInside_IsRefused()
    {
        var parsed = QueryRules.Document("""{"$or": [{"status": "paid"}, {"$where": "this.total > 1"}]}""", "filter");

        Assert.Equal(Messages.Forbidden("$where"), parsed.Error);
        Assert.Equal("notAllowed", parsed.Type);
    }

    [Theory]
    [InlineData("""[{"$out": "copy"}]""", "$out")]
    [InlineData("""[{"$match": {}}, {"$merge": {"into": "orders"}}]""", "$merge")]
    [InlineData("""[{"$group": {"_id": null, "x": {"$accumulator": {}}}}]""", "$accumulator")]
    [InlineData("""[{"$addFields": {"x": {"$function": {"body": "1", "args": [], "lang": "js"}}}}]""", "$function")]
    public void Pipeline_StageThatWritesOrRunsCode_IsRefused(string pipeline, string operation)
    {
        var parsed = QueryRules.Pipeline(pipeline, Sales);

        Assert.Equal(Messages.Forbidden(operation), parsed.Error);
        Assert.Equal("notAllowed", parsed.Type);
    }

    /// <summary>A collection kept out by name must not come in through a join.</summary>
    [Theory]
    [InlineData("""[{"$lookup": {"from": "salaries", "localField": "a", "foreignField": "b", "as": "c"}}]""")]
    [InlineData("""[{"$unionWith": "salaries"}]""")]
    [InlineData("""[{"$unionWith": {"coll": "salaries", "pipeline": []}}]""")]
    [InlineData("""[{"$lookup": {"from": "customers", "as": "c", "pipeline": [{"$unionWith": "salaries"}]}}]""")]
    [InlineData("""[{"$facet": {"all": [{"$lookup": {"from": "salaries", "localField": "a", "foreignField": "b", "as": "c"}}]}}]""")]
    [InlineData("""[{"$facet": {"x": [{"$unionWith": {"coll": "salaries"}}], "y": [{"$count": "n"}]}}]""")]
    [InlineData("""[{"$facet": {"x": [{"$lookup": {"from": "customers", "as": "c", "pipeline": [{"$facet": {"z": [{"$unionWith": "salaries"}]}}]}}]}}]""")]
    public void Pipeline_JoinOfACollectionTheSourceKeeps_IsRefusedNamingIt(string pipeline)
    {
        var parsed = QueryRules.Pipeline(pipeline, Sales);

        Assert.Equal(Messages.CollectionKept("sales", "salaries"), parsed.Error);
        Assert.Equal("notAllowed", parsed.Type);
    }

    [Fact]
    public void Pipeline_JoinOfAnAllowedCollection_IsAccepted()
    {
        var parsed = QueryRules.Pipeline("""[{"$lookup": {"from": "customers", "localField": "customer", "foreignField": "_id", "as": "who"}}, {"$limit": 5}]""", Sales);

        Assert.Equal(2, parsed.Value!.Count);
    }

    [Fact]
    public void Pipeline_EveryCollectionAllowed_AcceptsAnyJoin()
    {
        var open = Sales with { Collections = [Source.Every] };

        var parsed = QueryRules.Pipeline("""[{"$unionWith": "salaries"}]""", open);

        Assert.NotNull(parsed.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("""{"$match": {}}""")]
    [InlineData("""[1]""")]
    public void Pipeline_NotAnArrayOfStages_IsRefused(string pipeline)
    {
        var parsed = QueryRules.Pipeline(pipeline, Sales);

        Assert.Equal(Messages.NotAPipeline, parsed.Error);
        Assert.Equal(ToolFailures.Arguments, parsed.Type);
    }

    /// <summary>A join into another database names it as an object; no source allows that.</summary>
    [Fact]
    public void Pipeline_JoinIntoAnotherDatabase_IsRefused()
    {
        var parsed = QueryRules.Pipeline("""[{"$lookup": {"from": {"db": "hr", "coll": "salaries"}, "localField": "a", "foreignField": "b", "as": "c"}}]""", Sales);

        Assert.StartsWith(Messages.CollectionKept("sales", "{").Split('{')[0], parsed.Error, StringComparison.Ordinal);
        Assert.Equal("notAllowed", parsed.Type);
        Assert.Contains("hr", parsed.Error, StringComparison.Ordinal);
    }
}
