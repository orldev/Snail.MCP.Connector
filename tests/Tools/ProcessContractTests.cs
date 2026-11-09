using System.Net;

namespace Snail.MCP.Connector.Tests.Tools;

/// <summary>
/// The program as the platform starts it — the build beside the tests, or the publication <c>SNAIL_CONNECTOR_SERVER</c>
/// names — asked over HTTP.
/// </summary>
/// <remarks>No Vault address is set: no source is declared, and the tools have to say so in an answer of their own.</remarks>
public sealed class ProcessContractTests : IAsyncLifetime
{
    public const string ServerVariable = "SNAIL_CONNECTOR_SERVER";

    private HttpServerProgram _server = null!;

    /// <summary>The server reads its address from SNAIL_MCP_CONNECTOR_URL and is ready when /healthz answers.</summary>
    public async Task InitializeAsync() => _server = await HttpServerProgram.StartAsync("Snail.MCP.Connector", ServerVariable);

    public async Task DisposeAsync() => await _server.DisposeAsync();

    [Fact]
    public async Task ListTools_StartedProgram_OffersEveryTool()
    {
        await using var session = await _server.SessionAsync();

        var tools = await session.ListToolsAsync();

        Assert.Equal(11, tools.Count);
    }

    /// <summary>An export outlasting its wait is a job: its resource is offered, and a client is told of its end rather than made to ask.</summary>
    [Fact]
    public async Task Initialize_StartedProgram_OffersTheJobsOfItsExports()
    {
        await using var session = await _server.SessionAsync();

        var templates = await session.ListResourceTemplatesAsync();

        Assert.True(session.ServerCapabilities.Resources?.Subscribe);
        Assert.Contains(templates, template => template.UriTemplate.StartsWith("job://connector/", StringComparison.Ordinal));
    }

    /// <summary>The guard rules of every Snail server, on the list a model receives.</summary>
    [Fact]
    public async Task Tools_AsListed_KeepTheGuardRules()
    {
        await using var session = await _server.SessionAsync();

        var tools = await session.ListToolsAsync();

        Assert.Empty(ToolGuards.Problems(tools.Select(tool => tool.ProtocolTool)));
    }

    [Fact]
    public async Task CallTool_DiagnoseWithoutVault_SaysNoSourceIsDeclared()
    {
        await using var session = await _server.SessionAsync();

        var result = await session.CallToolAsync("connector_diagnose");

        Assert.True(result.IsOk(), result.Text());
        Assert.Contains("no Vault address", result.Data()?["note"]?.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_FindWithoutACopyInTheAddress_IsAToolErrorThatSaysWhereTheCopyComesFrom()
    {
        await using var session = await _server.SessionAsync();

        var result = await session.CallToolAsync("connector_find", new Dictionary<string, object?> { ["source"] = "sales", ["collection"] = "orders" });

        Assert.True(result.IsError);
        Assert.False(result.IsOk());
        Assert.Contains("?root=", result.Hint(), StringComparison.Ordinal);
    }

    /// <summary>A role given some of the tools sees those alone, and a call to another is refused in the envelope, flagged an error.</summary>
    [Fact]
    public async Task CallTool_ToolNotGrantedToTheSession_IsHiddenAndRefusedByName()
    {
        await using var session = await _server.SessionAsync("tool=connector_sources");

        var tools = await session.ListToolsAsync();
        var result = await session.CallToolAsync("connector_count", new Dictionary<string, object?> { ["source"] = "sales", ["collection"] = "orders" });

        Assert.Equal(["connector_sources"], tools.Select(tool => tool.Name));
        Assert.True(result.IsError);
        Assert.False(result.IsOk());
        Assert.Contains("'connector_count' was not granted", result.Error(), StringComparison.Ordinal);
        Assert.NotNull(result.Hint());
    }

    /// <summary>The dashboard's door answers nobody without the key: an agent that finds the address gets nothing from it.</summary>
    [Fact]
    public async Task Proposal_WithoutTheKey_IsNotFound()
    {
        using var http = new HttpClient { BaseAddress = _server.Address };

        using var answer = await http.PostAsync(new Uri("/proposals/w0123456789abcdef/decision", UriKind.Relative), new StringContent("""{"allow":true,"by":"agent"}""", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NotFound, answer.StatusCode);
    }

    /// <summary>
    /// The dashboard's door refuses a page in a browser as the MCP endpoint does: the dashboard asks it from the stand's
    /// own backend, and a page that reaches the address — DNS rebinding, a link — is turned away before the key is read.
    /// </summary>
    [Fact]
    public async Task ApprovalDoor_AsServed_RefusesBrowsersOnly() =>
        Assert.Empty(await HttpGuards.ProblemsAsync(new Uri(_server.Address, "/proposals/w0123456789abcdef/decision")));

    /// <summary>A page in a browser that reaches the server's address — DNS rebinding, a link — is refused before any tool, and an agent's client is not.</summary>
    [Fact]
    public async Task Endpoint_AsServed_RefusesBrowsersOnly() =>
        Assert.Empty(await HttpGuards.ProblemsAsync(_server.Endpoint));
}
