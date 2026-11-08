using System.Collections;
using Microsoft.Extensions.Configuration;
using Snail.MCP.Connector.Configuration;
using Snail.Sdk.Mcp.Hosting;

namespace Snail.MCP.Connector.Tests.Configuration;

/// <summary>
/// Where the server listens: loopback outside a container, since nothing but the stand's network keeps a caller from its
/// MCP endpoint. These tests run outside a container, as the server does under launchd.
/// </summary>
public sealed class ConnectorSettingsTests
{
    [Fact]
    public void EndpointOf_NothingSet_IsLoopbackAt8830OverHttp()
    {
        var endpoint = ConnectorSettings.EndpointOf(Settings());

        Assert.Equal(McpTransport.Http, endpoint.Transport);
        Assert.Equal(new Uri("http://127.0.0.1:8830/"), endpoint.Url);
        Assert.False(endpoint.ListensOnEveryInterface);
    }

    [Fact]
    public void EndpointOf_PortAlone_KeepsLoopback()
    {
        var endpoint = ConnectorSettings.EndpointOf(Settings(("SNAIL_MCP_CONNECTOR_PORT", "9930")));

        Assert.Equal(new Uri("http://127.0.0.1:9930/"), endpoint.Url);
    }

    /// <summary>Outside a container, every interface — by the flag, by 0.0.0.0, or by an address beyond loopback — is refused at start, naming the setting.</summary>
    [Theory]
    [InlineData("SNAIL_MCP_CONNECTOR_LISTENS_ON_EVERY_INTERFACE", "true")]
    [InlineData("SNAIL_MCP_CONNECTOR_URL", "http://0.0.0.0:8830/")]
    [InlineData("SNAIL_MCP_CONNECTOR_URL", "http://192.168.1.20:8830/")]
    public void EndpointOf_BeyondLoopbackOutsideAContainer_IsRefused(string variable, string value)
    {
        var refused = Assert.Throws<StartRefusedException>(() => ConnectorSettings.EndpointOf(Settings((variable, value))));

        Assert.StartsWith(variable, refused.Message, StringComparison.Ordinal);
        Assert.Contains("loopback", refused.Message, StringComparison.Ordinal);
        Assert.Contains(value, refused.Message, StringComparison.Ordinal);
    }

    private static IConfiguration Settings(params (string Name, string Value)[] variables)
    {
        var dictionary = new Hashtable();
        foreach (var (name, value) in variables)
            dictionary[name] = value;

        return ConnectorSettings.Read(file: null, dictionary);
    }
}
