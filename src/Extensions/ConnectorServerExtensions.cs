using Snail.MCP.Connector.Configuration;
using Snail.MCP.Connector.Tools;

namespace Snail.MCP.Connector.Extensions;

/// <summary>Composition root: which features the server has, and where it answers.</summary>
public static class ConnectorServerExtensions
{
    extension(WebApplicationBuilder builder)
    {
        /// <summary>The tools, the sources and the settings, read from the settings file and the <c>SNAIL_MCP_CONNECTOR_</c> variables only.</summary>
        public WebApplicationBuilder ConfigureConnectorServer()
        {
            builder.Services
                .AddOptions<ConnectorOptions>()
                .Bind(ConnectorSettings.Read())
                .ValidateOnStart();

            builder.Services.AddQueries();

            builder.Services
                .AddMcpServer(options =>
                {
                    options.ServerInfo = Snail.Sdk.Mcp.Hosting.ServerIdentity.Of(typeof(ConnectorServerExtensions).Assembly);
                    options.ServerInstructions = ServerGuidance.Instructions;
                })
                .RefuseWhatWasNotGranted()
                .WithHttpTransport()
                .WithTools<ConnectorTools>()
                .WithTools<DiagnosticsTools>();

            return builder;
        }
    }

    extension(WebApplication app)
    {
        /// <summary>The MCP endpoint and the health check.</summary>
        public WebApplication UseConnectorEndpoints()
        {
            app.MapMcp("/mcp");
            app.MapGet("/healthz", () => Results.Ok());

            return app;
        }
    }
}
