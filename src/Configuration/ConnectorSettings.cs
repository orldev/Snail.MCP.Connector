using System.Collections;
using Snail.Sdk.Mcp.Hosting;

namespace Snail.MCP.Connector.Configuration;

/// <summary>The settings <see cref="ConnectorOptions"/> is bound from: the server's settings file, then its <c>SNAIL_MCP_CONNECTOR_</c> variables, which win.</summary>
/// <remarks>
/// Built apart from the web host's own configuration on purpose: an <c>appsettings.json</c> or a variable of whoever
/// started the server that happens to share a name is not a setting of this server and must not become one by accident.
/// </remarks>
public static class ConnectorSettings
{
    /// <summary>The server's short name, which names its settings file and the prefix of its variables.</summary>
    public const string Name = "connector";

    /// <summary>Where the server listens: Url (<c>SNAIL_MCP_CONNECTOR_URL</c>) or Port (<c>SNAIL_MCP_CONNECTOR_PORT</c>), 127.0.0.1:8830 when unset, over HTTP only.</summary>
    /// <remarks>
    /// Inside the image (<c>DOTNET_RUNNING_IN_CONTAINER</c>) the port opens on every interface, the port the container
    /// publishes being the boundary; anywhere else on loopback only (<see cref="McpReach.LoopbackOrContainer"/>): the
    /// server holds no key of its own for its MCP endpoint, so <c>LISTENS_ON_EVERY_INTERFACE</c>, a <c>0.0.0.0</c> URL or
    /// one beyond loopback outside a container is refused at start, naming the variable.
    /// </remarks>
    public static McpEndpoint Endpoint => EndpointOf(Read());

    /// <summary>Where the server listens as <paramref name="settings"/> ask, refused when they ask for more than loopback outside a container.</summary>
    public static McpEndpoint EndpointOf(IConfiguration settings) =>
        McpEndpoint.ReadHttp(settings, new Uri("http://127.0.0.1:8830/"), new McpEndpointOptions { Reach = McpReach.LoopbackOrContainer, Server = Name });

    /// <summary>The settings as they stand: the settings file found, then the process's variables.</summary>
    public static IConfiguration Read() => Read(ServerSettings.FileOf(Name), Environment.GetEnvironmentVariables());

    /// <summary>The settings of <paramref name="file"/>, when there is one, under the server's variables among <paramref name="variables"/>.</summary>
    public static IConfiguration Read(string? file, IDictionary variables) =>
        new ConfigurationBuilder()
            .AddServerSettings(Name, file, variables)
            .Build();
}
