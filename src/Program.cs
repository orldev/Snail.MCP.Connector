using Snail.MCP.Connector.Configuration;
using Snail.MCP.Connector.Extensions;
using Snail.Sdk.Mcp.Hosting;
using Snail.Sdk.Mcp.Http;

try
{
    var app = McpHttpHost.CreateBuilder(args, ConnectorSettings.Endpoint)
        .ConfigureConnectorServer()
        .Build()
        .UseConnectorEndpoints();

    await app.RunAsync();

    return 0;
}
catch (StartRefusedException refused)
{
    await Console.Error.WriteLineAsync(refused.Message);

    return 1;
}
