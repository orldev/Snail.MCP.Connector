using Snail.MCP.Connector.Application;
using Snail.MCP.Connector.Configuration;
using Snail.MCP.Connector.Tools;
using Snail.Sdk.Mcp.Hosting.Jobs;
using Snail.Sdk.Mcp.Http;

namespace Snail.MCP.Connector.Extensions;

/// <summary>Composition root: which features the server has, and where it answers.</summary>
/// <remarks>
/// On the host of the standard (<c>McpHttpHost.CreateContainerBuilder</c>): the port on every interface of the container,
/// log lines on stderr, the heap trimmed when idle, a session for every client and the refusal of every request a
/// browser makes. A web host of the server's own answered a request with <c>Origin: http://evil.example</c> 200
/// (audit of 30.10.2026).
/// </remarks>
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
                .WithLocalHttpTransport()
                .WithTools<ConnectorTools>()
                .WithTools<WriteTools>()
                .WithTools<DiagnosticsTools>()
                .WithJobs(Short, OwnerOf);

            return builder;
        }
    }

    /// <summary>The server's short name, the first part of every job's URI.</summary>
    public const string Short = "connector";

    /// <summary>The owner of a request's jobs: the task its address names, so a step reads only its own task's exports, whichever session it opened.</summary>
    /// <remarks>A worker that connects again opens a new session; owned by the session, the export it was writing would be unknown to it.</remarks>
    private static string? OwnerOf(IServiceProvider services) => services.GetService<SessionCopy>()?.Current().Value?.Task;

    extension(WebApplication app)
    {
        /// <summary>The MCP endpoint and the health check.</summary>
        public WebApplication UseConnectorEndpoints()
        {
            app.MapLocalMcp("/mcp");
            app.MapGet("/healthz", () => Results.Ok());
            app.MapApprovals();

            return app;
        }
    }
}
