using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Snail.MCP.Connector.Application;

namespace Snail.MCP.Connector.Tools;

/// <summary>Writes to the company's databases: proposed by the agent, allowed by the stand's owner or an administrator, run once.</summary>
[McpServerToolType]
public sealed class WriteTools(Writes writes)
{
    /// <summary>Dry-runs a write and keeps it for a person to decide.</summary>
    [McpServerTool(Name = "connector_propose_write", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description(ToolDescriptions.ProposeWrite)]
    public async Task<CallToolResult> ProposeAsync(
        [Description(ToolDescriptions.SourceName)] string source,
        [Description(ToolDescriptions.Collection)] string collection,
        [Description(ToolDescriptions.Operation)] string operation,
        [Description(ToolDescriptions.Reason)] string reason,
        [Description(ToolDescriptions.WriteFilter)] string? filter = null,
        [Description(ToolDescriptions.Change)] string? change = null,
        CancellationToken cancellationToken = default) =>
        ToolResponse.From(await writes.ProposeAsync(source, collection, operation, filter, change, reason, cancellationToken));

    /// <summary>Runs an allowed proposal.</summary>
    [McpServerTool(Name = "connector_apply", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description(ToolDescriptions.Apply)]
    public async Task<CallToolResult> ApplyAsync(
        [Description(ToolDescriptions.Proposal)] string proposal,
        CancellationToken cancellationToken = default) =>
        ToolResponse.From(await writes.ApplyAsync(proposal, cancellationToken));
}
