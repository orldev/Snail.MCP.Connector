using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Snail.MCP.Connector.Application;
using Snail.MCP.Connector.Application.Ports;
using Snail.MCP.Connector.Configuration;

namespace Snail.MCP.Connector.Adapters;

/// <summary>The sources as Vault keeps them: one KV v2 secret per source under <see cref="ConnectorOptions.VaultPath"/>.</summary>
/// <remarks>
/// A secret holds <c>kind</c> (mongodb, sqlserver, postgresql), <c>database</c>, <c>collections</c> (comma-separated,
/// <c>*</c> for every one), <c>description</c>, <c>readUri</c> and, when a person declared a writing user, <c>writeUri</c>.
/// Read on every call rather than kept: a source a person changes on the stand is the one the next query reaches, and
/// Vault answers within the same network in milliseconds; the sources of a listing are read at once, not one by one.
/// A source a secret declares without what it needs is left out of a listing and named in its note, not dropped
/// silently. The connection strings never leave this adapter but in a
/// <see cref="SourceAccess"/>; a listing carries none.
/// </remarks>
public sealed class VaultSources(IHttpClientFactory clients, IOptions<ConnectorOptions> options) : ISources
{
    /// <summary>The name of the HTTP client Vault is called with.</summary>
    public const string Client = "vault";

    /// <inheritdoc />
    public async Task<Answer<DeclaredSources>> ListAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.Value.VaultAddress))
            return Answer.Failed(Messages.NoVault, Messages.NoVaultHint, FailureTypes.NotConfigured);

        var listed = await ReadAsync($"metadata/{options.Value.VaultPath}?list=true", cancellationToken);
        if (!listed.IsSuccess)
            return listed.As<DeclaredSources>();

        IReadOnlyList<string> names = [.. (listed.Value?["data"]?["keys"] as JsonArray ?? []).Select(key => key?.GetValue<string>() ?? string.Empty).Where(IsName)];
        if (names.Count == 0)
            return Answer.Failed(Messages.NoSources, Messages.NoSourcesHint, FailureTypes.NoSources);

        IReadOnlyList<string> ordered = [.. names.Order(StringComparer.Ordinal)];
        var found = await Task.WhenAll(ordered.Select(name => FindAsync(name, cancellationToken)));

        IReadOnlyList<Source> sources = [.. found.Where(source => source.Value is not null).Select(source => source.Value!.Source)];
        IReadOnlyList<string> unread = [.. found.Where(source => source.Value is null).Select(source => source.Error!)];

        return Answer.Ok(new DeclaredSources(sources, unread));
    }

    /// <inheritdoc />
    public async Task<Answer<ReachableSource>> FindAsync(string name, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.Value.VaultAddress))
            return Answer.Failed(Messages.NoVault, Messages.NoVaultHint, FailureTypes.NotConfigured);

        if (!IsName(name))
            return Answer.Failed(Messages.NoSource(name), Messages.NoSourceHint, FailureTypes.NoSuchSource);

        var read = await ReadAsync($"data/{options.Value.VaultPath}/{name}", cancellationToken);
        if (!read.IsSuccess)
            return read.As<ReachableSource>();

        if (read.Value?["data"]?["data"] is not JsonObject secret)
            return Answer.Failed(Messages.NoSource(name), Messages.NoSourceHint, FailureTypes.NoSuchSource);

        return Of(name, secret);
    }

    /// <summary>The source a secret declares, or what it lacks.</summary>
    public static Answer<ReachableSource> Of(string name, JsonObject secret)
    {
        string? Text(string key) => secret[key] is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;

        if (Kind(Text("kind")) is not { } kind)
            return Answer.Failed(Messages.SourceIncomplete(name, "kind: mongodb, sqlserver or postgresql"), type: FailureTypes.SourceIncomplete);

        if (Text("database") is not { } database)
            return Answer.Failed(Messages.SourceIncomplete(name, "database"), type: FailureTypes.SourceIncomplete);

        if (Text("readUri") is not { } read)
            return Answer.Failed(Messages.SourceIncomplete(name, "readUri"), type: FailureTypes.SourceIncomplete);

        IReadOnlyList<string> collections = [.. (Text("collections") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        string? write = Text("writeUri");
        var source = new Source(name, kind, database, collections) { Description = Text("description"), CanWrite = write is not null };

        return Answer.Ok(new ReachableSource(source, new SourceAccess(read, write)));
    }

    private static SourceKind? Kind(string? word) => word?.ToLowerInvariant() switch
    {
        "mongodb" => SourceKind.MongoDb,
        "sqlserver" => SourceKind.SqlServer,
        "postgresql" => SourceKind.PostgreSql,
        _ => null
    };

    private static bool IsName(string name) =>
        name.Length is > 0 and <= 63 && !name.EndsWith('/') && name.All(letter => char.IsAsciiLetterOrDigit(letter) || letter is '-' or '_');

    private async Task<Answer<JsonObject>> ReadAsync(string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{options.Value.VaultAddress.TrimEnd('/')}/v1/{options.Value.VaultMount.Trim('/')}/{path}");
        request.Headers.Add("X-Vault-Token", options.Value.VaultToken);

        try
        {
            using var response = await clients.CreateClient(Client).SendAsync(request, cancellationToken);
            if (response.StatusCode is HttpStatusCode.NotFound)
                return Answer.Ok(new JsonObject());

            if (!response.IsSuccessStatusCode)
                return Answer.Failed(Messages.VaultSaid($"{(int)response.StatusCode} {response.ReasonPhrase}"), Messages.NoVaultHint, FailureTypes.VaultError);

            return Answer.Ok(await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken) ?? []);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return Answer.Failed(Messages.VaultSaid(exception.Message), Messages.NoVaultHint, FailureTypes.VaultError);
        }
    }
}
