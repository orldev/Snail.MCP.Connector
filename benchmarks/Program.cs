using BenchmarkDotNet.Running;
using Snail.MCP.Connector.Benchmarks;

BenchmarkSwitcher.FromAssembly(typeof(QueryBenchmarks).Assembly).Run(args);
