using BenchmarkDotNet.Attributes;
using ModelContextProtocol.Protocol;
using Snail.MCP.Connector.Tools;

namespace Snail.MCP.Connector.Benchmarks;

/// <summary>The answer of a tool, measured; the shared perf workflow compares its allocations with the baseline.</summary>
[MemoryDiagnoser]
public class DiagnosticsBenchmarks
{
    /// <summary>One diagnosis.</summary>
    [Benchmark]
    public CallToolResult Diagnose() => DiagnosticsTools.Diagnose();
}
