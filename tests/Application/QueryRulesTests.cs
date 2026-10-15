using Snail.MCP.Connector.Application;

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
        Assert.Equal(Messages.ExtendedJsonHint, parsed.Hint);
    }

    /// <summary>$where runs JavaScript inside the database, however deep in the filter it hides.</summary>
    [Fact]
    public void Document_WhereDeepInside_IsRefused()
    {
        var parsed = QueryRules.Document("""{"$or": [{"status": "paid"}, {"$where": "this.total > 1"}]}""", "filter");

        Assert.Equal(Messages.Forbidden("$where"), parsed.Error);
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
    }

    /// <summary>A collection kept out by name must not come in through a join.</summary>
    [Theory]
    [InlineData("""[{"$lookup": {"from": "salaries", "localField": "a", "foreignField": "b", "as": "c"}}]""")]
    [InlineData("""[{"$unionWith": "salaries"}]""")]
    [InlineData("""[{"$unionWith": {"coll": "salaries", "pipeline": []}}]""")]
    [InlineData("""[{"$lookup": {"from": "customers", "as": "c", "pipeline": [{"$unionWith": "salaries"}]}}]""")]
    public void Pipeline_JoinOfACollectionTheSourceKeeps_IsRefusedNamingIt(string pipeline)
    {
        var parsed = QueryRules.Pipeline(pipeline, Sales);

        Assert.Equal(Messages.CollectionKept("sales", "salaries"), parsed.Error);
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
    }
}
