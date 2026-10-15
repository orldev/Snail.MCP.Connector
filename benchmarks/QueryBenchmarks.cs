using BenchmarkDotNet.Attributes;
using Snail.MCP.Connector.Application;
using Snail.MCP.Connector.Domain;

namespace Snail.MCP.Connector.Benchmarks;

/// <summary>The work a query does on the server itself before it reaches the database: checking a long pipeline against the rules.</summary>
[MemoryDiagnoser]
public class QueryBenchmarks
{
    private static readonly Source Sales = new("sales", SourceKind.MongoDb, "sales", ["orders", "customers"]);

    private static readonly string Long = Pipeline();

    /// <summary>A pipeline of two hundred stages and a join, checked.</summary>
    [Benchmark]
    public bool IsPipelineAccepted() => QueryRules.Pipeline(Long, Sales).Value is not null;

    private static string Pipeline()
    {
        IEnumerable<string> matches = Enumerable.Range(0, 200).Select(index => $"{{\"$match\":{{\"n{index}\":{{\"$gt\":{index}}}}}}}");
        const string join = """{"$lookup":{"from":"customers","localField":"c","foreignField":"_id","as":"who"}}""";

        return $"[{string.Join(',', matches)},{join}]";
    }
}
