using Snail.MCP.Connector.Configuration;
using Snail.MCP.Connector.Extensions;
using Snail.Sdk.Mcp.Http;

var app = McpHttpHost.CreateContainerBuilder(args, ConnectorSettings.Url.Port)
    .ConfigureConnectorServer()
    .Build()
    .UseConnectorEndpoints();

await app.RunAsync();
