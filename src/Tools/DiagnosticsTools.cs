using System.ComponentModel;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Snail.MCP.Connector.Application.Ports;
using Snail.MCP.Connector.Configuration;

namespace Snail.MCP.Connector.Tools;

/// <summary>Whether the server can reach the stand's sources.</summary>
[McpServerToolType]
public sealed class DiagnosticsTools(ISources sources, IOptions<ConnectorOptions> options)
{
    /// <summary>Says whether the sources can be read.</summary>
    [McpServerTool(Name = "connector_diagnose", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(ToolDescriptions.Diagnose)]
    public async Task<CallToolResult> DiagnoseAsync(CancellationToken cancellationToken = default)
    {
        var listed = await sources.ListAsync(cancellationToken);
        var settings = options.Value;
        var state = new JsonObject
        {
            ["server"] = typeof(DiagnosticsTools).Assembly.GetName().Version?.ToString(3),
            ["vault"] = string.IsNullOrWhiteSpace(settings.VaultAddress) ? null : settings.VaultAddress,
            ["sources"] = listed.Value?.Count ?? 0,
            ["note"] = listed.Error,
            ["mostRows"] = settings.MostRows,
            ["querySeconds"] = settings.QuerySeconds
        };

        return ToolResponse.Success(state);
    }
}
