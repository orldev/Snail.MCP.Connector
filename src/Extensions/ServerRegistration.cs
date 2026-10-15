using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Snail.MCP.Connector.Tools;
using Snail.Sdk.Mcp.Hosting;

namespace Snail.MCP.Connector.Extensions;

/// <summary>What the server says about itself and which tools it offers, the same over every transport.</summary>
public static class ServerRegistration
{
    /// <summary>The words a client shows the model before any tool: what the server is for and where to start.</summary>
    public const string Instructions = "Company databases for the platform's agents: MongoDB, SQL Server and PostgreSQL, read by a read-only user, written only after a human's approval. Start with connector_diagnose.";

    /// <summary>The server's name, version and instructions.</summary>
    public static void Describe(McpServerOptions server)
    {
        server.ServerInfo = ServerIdentity.Of(typeof(ServerRegistration).Assembly);
        server.ServerInstructions = Instructions;
    }

    extension(IMcpServerBuilder builder)
    {
        /// <summary>Every tool of the server; a native build keeps the methods of the classes named here.</summary>
        public IMcpServerBuilder WithServerTools() => builder.WithTools<DiagnosticsTools>();
    }
}
