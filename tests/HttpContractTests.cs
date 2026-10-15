using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ModelContextProtocol.Client;
using Snail.Sdk.Mcp.Testing;

namespace Snail.MCP.Connector.Tests;

/// <summary>The server over HTTP, started as a program on a free loopback port: the endpoint, its tools, the refusal of browsers.</summary>
public sealed class HttpContractTests : IAsyncLifetime
{
    private const string Assembly = "Snail.MCP.Connector";

    private const string Variable = "SNAIL_CONNECTOR_SERVER";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly Uri _url = new($"http://127.0.0.1:{FreePort()}/");

    private Process? _server;

    public async Task InitializeAsync()
    {
        var (command, arguments) = ServerProgram.Command(Assembly, Variable);
        var start = new ProcessStartInfo(command);

        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        start.Environment["HOME"] = ServerProgram.Home;
        start.Environment["SNAIL_MCP_CONNECTOR_TRANSPORT"] = "http";
        start.Environment["SNAIL_MCP_CONNECTOR_URL"] = _url.ToString();

        _server = Process.Start(start);

        await ListeningAsync();
    }

    public Task DisposeAsync()
    {
        _server?.Kill(entireProcessTree: true);
        _server?.Dispose();

        return Task.CompletedTask;
    }

    [Fact]
    public async Task Tools_OverHttp_KeepTheGuardRules()
    {
        await using var client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(_url, "mcp") }));

        var listed = await client.ListToolsAsync();

        Assert.Empty(ToolGuards.Problems(listed.Select(tool => tool.ProtocolTool)));
    }

    [Fact]
    public async Task Request_FromABrowser_IsRefused()
    {
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_url, "mcp"));

        request.Headers.Add("Origin", "https://example.com");

        using var answer = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, answer.StatusCode);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);

        listener.Start();

        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private async Task ListeningAsync()
    {
        using var probe = new HttpClient();
        var deadline = DateTime.UtcNow + Patience;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var answer = await probe.GetAsync(new Uri(_url, "mcp"));

                return;
            }
            catch (HttpRequestException)
            {
                await Task.Delay(200);
            }
        }

        throw new TimeoutException($"the server did not listen on {_url} within {Patience.TotalSeconds:0} s");
    }
}
