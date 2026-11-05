using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ModelContextProtocol.Client;
using Snail.Sdk.Mcp.Hosting;

namespace Snail.MCP.Connector.Tests.Tools;

/// <summary>
/// The program as the platform starts it — the build beside the tests, or the publication <c>SNAIL_CONNECTOR_SERVER</c>
/// names — asked over HTTP.
/// </summary>
/// <remarks>No Vault address is set: no source is declared, and the tools have to say so in an answer of their own.</remarks>
public sealed class ProcessContractTests : IAsyncLifetime
{
    public const string ServerVariable = "SNAIL_CONNECTOR_SERVER";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly Uri _address = new($"http://127.0.0.1:{FreePort()}");
    private Process _server = default!;

    public async Task InitializeAsync()
    {
        var (command, arguments) = ServerProgram.Command("Snail.MCP.Connector", ServerVariable);
        var start = new ProcessStartInfo(command, arguments)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        start.Environment["SNAIL_MCP_CONNECTOR_URL"] = _address.ToString();
        start.Environment[ServerSettings.FileVariableOf("connector")] = ServerProgram.NoSettingsFile;

        _server = Process.Start(start)!;
        _server.OutputDataReceived += (_, _) => { };
        _server.ErrorDataReceived += (_, _) => { };
        _server.BeginOutputReadLine();
        _server.BeginErrorReadLine();

        using var waiting = new CancellationTokenSource(Timeout);
        using var http = new HttpClient { BaseAddress = _address };

        while (true)
        {
            try
            {
                using var answer = await http.GetAsync(new Uri("/healthz", UriKind.Relative), waiting.Token);

                if (answer.IsSuccessStatusCode)
                    return;
            }
            catch (HttpRequestException) when (!_server.HasExited)
            {
            }

            await Task.Delay(100, waiting.Token);
        }
    }

    public Task DisposeAsync()
    {
        _server.Kill(entireProcessTree: true);
        _server.WaitForExit();
        _server.Dispose();

        return Task.CompletedTask;
    }

    [Fact]
    public async Task ListTools_StartedProgram_OffersEveryTool()
    {
        using var cancellation = new CancellationTokenSource(Timeout);
        await using var session = await SessionAsync(null, cancellation.Token);

        var tools = await session.ListToolsAsync(cancellationToken: cancellation.Token);

        Assert.Equal(11, tools.Count);
    }

    /// <summary>An export outlasting its wait is a job: its resource is offered, and a client is told of its end rather than made to ask.</summary>
    [Fact]
    public async Task Initialize_StartedProgram_OffersTheJobsOfItsExports()
    {
        using var cancellation = new CancellationTokenSource(Timeout);
        await using var session = await SessionAsync(null, cancellation.Token);

        var templates = await session.ListResourceTemplatesAsync(cancellationToken: cancellation.Token);

        Assert.True(session.ServerCapabilities.Resources?.Subscribe);
        Assert.Contains(templates, template => template.UriTemplate.StartsWith("job://connector/", StringComparison.Ordinal));
    }

    /// <summary>The guard rules of every Snail server, on the list a model receives.</summary>
    [Fact]
    public async Task Tools_AsListed_KeepTheGuardRules()
    {
        using var cancellation = new CancellationTokenSource(Timeout);
        await using var session = await SessionAsync(null, cancellation.Token);

        var tools = await session.ListToolsAsync(cancellationToken: cancellation.Token);

        Assert.Empty(ToolGuards.Problems(tools.Select(tool => tool.ProtocolTool)));
    }

    [Fact]
    public async Task CallTool_DiagnoseWithoutVault_SaysNoSourceIsDeclared()
    {
        using var cancellation = new CancellationTokenSource(Timeout);
        await using var session = await SessionAsync(null, cancellation.Token);

        var result = await session.CallToolAsync("connector_diagnose", cancellationToken: cancellation.Token);

        Assert.NotEqual(true, result.IsError);
        Assert.Contains("no Vault address", result.Text(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_FindWithoutACopyInTheAddress_IsAToolErrorThatSaysWhereTheCopyComesFrom()
    {
        using var cancellation = new CancellationTokenSource(Timeout);
        await using var session = await SessionAsync(null, cancellation.Token);

        var result = await session.CallToolAsync("connector_find", new Dictionary<string, object?> { ["source"] = "sales", ["collection"] = "orders" }, cancellationToken: cancellation.Token);

        Assert.True(result.IsError);
        Assert.Contains("?root=", result.Text(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_ToolNotGrantedToTheSession_IsRefusedByName()
    {
        using var cancellation = new CancellationTokenSource(Timeout);
        await using var session = await SessionAsync("tool=connector_sources", cancellation.Token);

        var result = await session.CallToolAsync("connector_count", new Dictionary<string, object?> { ["source"] = "sales", ["collection"] = "orders" }, cancellationToken: cancellation.Token);

        Assert.True(result.IsError);
        Assert.Contains("'connector_count' was not granted", result.Text(), StringComparison.Ordinal);
    }

    /// <summary>The dashboard's door answers nobody without the key: an agent that finds the address gets nothing from it.</summary>
    [Fact]
    public async Task Proposal_WithoutTheKey_IsNotFound()
    {
        using var http = new HttpClient { BaseAddress = _address };

        using var answer = await http.PostAsync(new Uri("/proposals/w0123456789abcdef/decision", UriKind.Relative), new StringContent("""{"allow":true,"by":"agent"}""", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NotFound, answer.StatusCode);
    }

    /// <summary>A page in a browser that reaches the server's address — DNS rebinding, a link — is refused before any tool.</summary>
    [Fact]
    public async Task Request_FromABrowser_IsRefused()
    {
        using var http = new HttpClient { BaseAddress = _address };
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/mcp?root=/workspaces/t1/step-0", UriKind.Relative))
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"page","version":"1"}}}""", System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Origin", "http://evil.example");
        request.Headers.Add("Accept", "application/json, text/event-stream");

        using var answer = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, answer.StatusCode);
    }

    private Task<McpClient> SessionAsync(string? query, CancellationToken cancellationToken) =>
        McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(_address, query is null ? "/mcp" : $"/mcp?{query}") }), cancellationToken: cancellationToken);

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
