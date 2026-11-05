using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Snail.MCP.Connector.Adapters;
using Snail.MCP.Connector.Application;
using Snail.MCP.Connector.Configuration;

namespace Snail.MCP.Connector.Tests.Adapters;

/// <summary>The sources as Vault keeps them: one secret each, read on every call, never listed with their credentials.</summary>
public sealed class VaultSourcesTests
{
    [Fact]
    public void Of_CompleteSecret_IsTheSourceWithBothUsers()
    {
        var secret = new JsonObject
        {
            ["kind"] = "mongodb",
            ["database"] = "sales",
            ["collections"] = "orders, customers",
            ["description"] = "Orders since 2019",
            ["readUri"] = "mongodb://reader:r@db/sales",
            ["writeUri"] = "mongodb://writer:w@db/sales"
        };

        var read = VaultSources.Of("sales", secret);

        var source = read.Value!.Source;
        Assert.Equal(SourceKind.MongoDb, source.Kind);
        Assert.Equal(["orders", "customers"], source.Collections);
        Assert.True(source.CanWrite);
        Assert.Equal("mongodb://reader:r@db/sales", read.Value.Access.ReadUri);
    }

    [Theory]
    [InlineData("kind")]
    [InlineData("database")]
    [InlineData("readUri")]
    public void Of_SecretWithoutWhatItNeeds_SaysWhatIsMissing(string missing)
    {
        var secret = new JsonObject { ["kind"] = "mongodb", ["database"] = "sales", ["readUri"] = "mongodb://reader@db/sales" };
        secret.Remove(missing);

        var read = VaultSources.Of("sales", secret);

        Assert.Null(read.Value);
        Assert.Contains(missing, read.Error, StringComparison.Ordinal);
    }

    /// <summary>A listing names the sources and never the users they are read as.</summary>
    [Fact]
    public async Task ListAsync_TwoSecretsAndAFolder_ListsTheTwoWithoutCredentials()
    {
        var vault = new Vault(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/v1/secret/metadata/connectors?list=true"] = """{"data":{"keys":["crm","sales","archive/"]}}""",
            ["/v1/secret/data/connectors/crm"] = """{"data":{"data":{"kind":"mongodb","database":"crm","collections":"*","readUri":"mongodb://reader:secret@db/crm"}}}""",
            ["/v1/secret/data/connectors/sales"] = """{"data":{"data":{"kind":"mongodb","database":"sales","collections":"orders","readUri":"mongodb://reader:secret@db/sales"}}}"""
        });

        var listed = await Sources(vault).ListAsync(CancellationToken.None);

        Assert.Equal(["crm", "sales"], listed.Value!.Select(source => source.Name));
        Assert.All(vault.Tokens, token => Assert.Equal("s.reader", token));
    }

    /// <summary>A secret a person filled in half is named with what it lacks, rather than missing from the list without a word.</summary>
    [Fact]
    public async Task ListAsync_SecretWithoutWhatItNeeds_IsLeftOutAndNamed()
    {
        var vault = new Vault(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/v1/secret/metadata/connectors?list=true"] = """{"data":{"keys":["crm","half"]}}""",
            ["/v1/secret/data/connectors/crm"] = """{"data":{"data":{"kind":"mongodb","database":"crm","collections":"*","readUri":"mongodb://reader:secret@db/crm"}}}""",
            ["/v1/secret/data/connectors/half"] = """{"data":{"data":{"kind":"mongodb","database":"half"}}}"""
        });

        var listed = await Sources(vault).ListAsync(CancellationToken.None);

        Assert.Equal(["crm"], listed.Value!.Select(source => source.Name));
        Assert.Contains(Messages.SourceIncomplete("half", "readUri"), listed.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListAsync_NothingDeclared_SaysSoAndHowOneIsDeclared()
    {
        var listed = await Sources(new Vault([])).ListAsync(CancellationToken.None);

        Assert.Equal(Messages.NoSources, listed.Error);
    }

    [Fact]
    public async Task FindAsync_NoVaultAddress_SaysHowToSetIt()
    {
        var sources = new VaultSources(new Clients(new Vault([])), Options.Create(new ConnectorOptions()));

        var found = await sources.FindAsync("sales", CancellationToken.None);

        Assert.Equal(Messages.NoVault, found.Error);
    }

    [Theory]
    [InlineData("../sys")]
    [InlineData("sales/x")]
    public async Task FindAsync_NameThatIsAPath_IsNoSource(string name)
    {
        var vault = new Vault([]);

        var found = await Sources(vault).FindAsync(name, CancellationToken.None);

        Assert.Equal(Messages.NoSource(name), found.Error);
        Assert.Empty(vault.Tokens);
    }

    private static VaultSources Sources(Vault vault) =>
        new(new Clients(vault), Options.Create(new ConnectorOptions { VaultAddress = "http://vault:8200", VaultToken = "s.reader" }));

    private sealed class Clients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Vault(Dictionary<string, string> answers) : HttpMessageHandler
    {
        public List<string> Tokens { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Tokens)
                Tokens.Add(request.Headers.GetValues("X-Vault-Token").Single());

            var answer = answers.TryGetValue(request.RequestUri!.PathAndQuery, out string? body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound);

            return Task.FromResult(answer);
        }
    }
}
