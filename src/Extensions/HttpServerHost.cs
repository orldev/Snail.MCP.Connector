using Microsoft.Extensions.DependencyInjection;
using Snail.Sdk.Mcp.Http;

namespace Snail.MCP.Connector.Extensions;

/// <summary>One server for every session of the machine, on a loopback address, kept running by launchd.</summary>
/// <remarks>The loopback address and the refusal of browsers are Snail.Sdk.Mcp.Http's.</remarks>
public static class HttpServerHost
{
    /// <summary>Runs the server over Streamable HTTP until the system ends it.</summary>
    public static Task RunAsync(string[] args)
    {
        var builder = McpHttpHost.CreateBuilder(args, ServerSettings.Url);

        builder.Services.AddMcpServer(ServerRegistration.Describe).WithLocalHttpTransport().WithServerTools();

        var app = builder.Build();

        app.MapLocalMcp();

        return app.RunAsync();
    }
}
