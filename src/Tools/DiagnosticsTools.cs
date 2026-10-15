using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Snail.MCP.Connector.Tools;

/// <summary>The state of the server: the sample tool a new server starts from.</summary>
[McpServerToolType]
public sealed class DiagnosticsTools
{
    /// <summary>Reports the server's name, version and runtime.</summary>
    [McpServerTool(Name = "connector_diagnose", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Say what state this server is in: its name, its version and the runtime it runs on. Changes nothing.")]
    public static CallToolResult Diagnose()
    {
        var assembly = typeof(DiagnosticsTools).Assembly.GetName();
        var state = new JsonObject
        {
            ["ok"] = true,
            ["server"] = assembly.Name,
            ["version"] = assembly.Version?.ToString(3),
            ["runtime"] = Environment.Version.ToString()
        };

        return new CallToolResult { Content = [new TextContentBlock { Text = state.ToJsonString() }] };
    }
}
