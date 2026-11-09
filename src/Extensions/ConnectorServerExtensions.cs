using Microsoft.Extensions.Options;
using Snail.MCP.Connector.Adapters;
using Snail.MCP.Connector.Application;
using Snail.MCP.Connector.Configuration;
using Snail.MCP.Connector.Tools;
using Snail.Sdk.Mcp.Hosting;
using Snail.Sdk.Mcp.Hosting.Jobs;
using Snail.Sdk.Mcp.Hosting.Sessions;
using Snail.Sdk.Mcp.Http;

namespace Snail.MCP.Connector.Extensions;

/// <summary>Composition root: which features the server has, and where it answers.</summary>
/// <remarks>
/// On the host of the standard (<c>McpHttpHost.CreateBuilder</c> over <see cref="ConnectorSettings.Endpoint"/>): the port
/// on every interface of the container, log lines on stderr, the heap trimmed when idle, a session for every client and
/// the refusal of every request a browser makes. A web host of the server's own answered a request with
/// <c>Origin: http://evil.example</c> 200 (audit of 30.10.2026). The grant of a session (<c>?tool=</c>) is the SDK's: a
/// tool it does not name is left out of the list, and a call to it is refused in the envelope, type <c>notGranted</c>.
/// A failure nobody foresaw is answered with its message scrubbed of the credentials a connection string carries: a
/// driver that throws past an adapter quotes the string it was given, and a job's failure is worded the same way.
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
                    options.ServerInfo = ServerIdentity.Of(typeof(ConnectorServerExtensions).Assembly);
                    options.ServerInstructions = ServerGuidance.Instructions;
                })
                .ExplainFailures(options => options.Error = (_, failure) => SqlTables.Scrubbed(failure.Message))
                .WithLocalHttpTransport()
                .WithSessionGrant()
                .WithTools<ConnectorTools>()
                .WithTools<WriteTools>()
                .WithTools<DiagnosticsTools>()
                .WithJobs(ConnectorSettings.Name, OwnerOf);

            return builder;
        }
    }

    /// <summary>The owner of a request's jobs: the task its address names, so a step reads only its own task's exports, whichever session it opened.</summary>
    /// <remarks>A worker that connects again opens a new session; owned by the session, the export it was writing would be unknown to it.</remarks>
    private static readonly Func<IServiceProvider, string?> OwnerOf =
        JobOwners.TaskOfCopy(services => services.GetRequiredService<IOptions<ConnectorOptions>>().Value.WorkspacesRoot, SessionCopy.IsTaskId);

    extension(WebApplication app)
    {
        /// <summary>The MCP endpoint, the health check and the dashboard's door.</summary>
        public WebApplication UseConnectorEndpoints()
        {
            app.MapLocalMcp();
            app.MapHealth();
            app.MapApprovals();

            return app;
        }
    }
}
