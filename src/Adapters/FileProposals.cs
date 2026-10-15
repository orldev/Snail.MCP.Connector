using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Snail.MCP.Connector.Application.Ports;
using Snail.MCP.Connector.Configuration;
using Snail.Sdk.Mcp.Hosting;

namespace Snail.MCP.Connector.Adapters;

/// <summary>Proposals to write as one JSON file each in the server's data folder, so a restart between the decision and the write forgets none.</summary>
public sealed partial class FileProposals(string folder) : IProposals
{
    /// <summary>The proposals in the server's own data folder.</summary>
    public static FileProposals InDataFolder() => new(Path.Combine(ServerFolders.DataDirectory(ConnectorSettings.Name), "proposals"));

    /// <inheritdoc />
    public async Task SaveAsync(WriteProposal proposal, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(folder);
        string file = FileOf(proposal.Id)!;
        string written = $"{file}.{Guid.NewGuid():N}";

        await File.WriteAllTextAsync(written, JsonSerializer.Serialize(proposal, ProposalJson.Default.WriteProposal), cancellationToken);
        File.Move(written, file, overwrite: true);
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
