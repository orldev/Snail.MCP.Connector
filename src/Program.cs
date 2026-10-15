using Snail.MCP.Connector.Extensions;

var app = WebApplication.CreateBuilder(args)
    .ConfigureConnectorServer()
    .Build()
    .UseConnectorEndpoints();

await app.RunAsync();

/// <summary>The entry point, named so the contract tests can host the server in memory.</summary>
public partial class Program;
