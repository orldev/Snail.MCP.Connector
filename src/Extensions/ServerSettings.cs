namespace Snail.MCP.Connector.Extensions;

/// <summary>The settings of the server, read from SNAIL_MCP_CONNECTOR_ variables.</summary>
public static class ServerSettings
{
    /// <summary>The prefix of every variable the server reads.</summary>
    public const string Prefix = "SNAIL_MCP_CONNECTOR_";

    /// <summary>Where the HTTP endpoint listens: SNAIL_MCP_CONNECTOR_URL, a loopback address.</summary>
    public static Uri Url => new(Environment.GetEnvironmentVariable($"{Prefix}URL") ?? "http://127.0.0.1:8830/");
}
