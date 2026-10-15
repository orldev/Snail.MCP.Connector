using BenchmarkDotNet.Running;
using Snail.MCP.Connector.Benchmarks;

BenchmarkSwitcher.FromAssembly(typeof(DiagnosticsBenchmarks).Assembly).Run(args);
