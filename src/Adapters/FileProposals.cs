using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Snail.MCP.Connector.Application.Ports;
using Snail.MCP.Connector.Configuration;
using Snail.Sdk.Mcp.Hosting;

namespace Snail.MCP.Connector.Adapters;

/// <summary>Proposals to write as one JSON file each in the server's data folder, so a restart between the decision and the write forgets none.</summary>
/// <remarks>
/// A proposal carries a sample of the company's rows, so it is kept no longer than the journal is: a file not written
/// for <paramref name="days"/> goes, checked once a day. No days keeps them all.
/// </remarks>
/// <param name="folder">Where the files are.</param>
/// <param name="days">How long a proposal is kept after it was last written; 0 keeps every one.</param>
public sealed partial class FileProposals(string folder, int days = 0) : IProposals
{
    private DateOnly _forgotten;

    /// <summary>The proposals in the server's own data folder, kept as long as the journal.</summary>
    public static FileProposals InDataFolder(IOptions<ConnectorOptions> options) =>
        new(Path.Combine(ServerFolders.DataDirectory(ConnectorSettings.Name), "proposals"), options.Value.JournalDays);

    /// <inheritdoc />
    public async Task SaveAsync(WriteProposal proposal, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(folder);
        string file = FileOf(proposal.Id)!;
        string written = $"{file}.{Guid.NewGuid():N}";

        await File.WriteAllTextAsync(written, JsonSerializer.Serialize(proposal, ProposalJson.Default.WriteProposal), cancellationToken);
        File.Move(written, file, overwrite: true);

        Forget();
    }

    private void Forget()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (days <= 0 || today == _forgotten)
            return;

        _forgotten = today;
        var stale = DateTime.UtcNow.AddDays(-days);

        try
        {
            foreach (string file in Directory.EnumerateFiles(folder, "*.json").Where(file => File.GetLastWriteTimeUtc(file) < stale))
                File.Delete(file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _forgotten = default;
        }
    }

    /// <inheritdoc />
    public async Task<WriteProposal?> FindAsync(string id, CancellationToken cancellationToken)
    {
        if (FileOf(id) is not { } file || !File.Exists(file))
            return null;

        return JsonSerializer.Deserialize(await File.ReadAllTextAsync(file, cancellationToken), ProposalJson.Default.WriteProposal);
    }

    /// <summary>The proposal's file; null for an id that is not one this server makes, so no id reaches outside the folder.</summary>
    private string? FileOf(string id) => Id().IsMatch(id) ? Path.Combine(folder, $"{id}.json") : null;

    [GeneratedRegex("^w[0-9a-f]{16}$")]
    private static partial Regex Id();
}

/// <summary>Proposals as JSON, generated at build time.</summary>
[JsonSerializable(typeof(WriteProposal))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
internal sealed partial class ProposalJson : JsonSerializerContext;
