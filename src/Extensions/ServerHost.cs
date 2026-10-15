namespace Snail.MCP.Connector.Extensions;

/// <summary>The server as a running host, on the transport it was made for.</summary>
public static class ServerHost
{
    /// <summary>Runs the server until the client or the system ends it.</summary>
    public static Task RunAsync(string[] args)
    {
        return HttpServerHost.RunAsync(args);
    }
}
