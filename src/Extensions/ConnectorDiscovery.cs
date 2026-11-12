using Snail.MCP.Connector.Configuration;
using Snail.Sdk.Mcp.Hosting.Discovery;

namespace Snail.MCP.Connector.Extensions;

/// <summary>How a model reaches this server's tools without knowing their names: <c>find_tool</c>, and the commands <c>run_tools</c> and <c>run_program</c> send.</summary>
/// <remarks>
/// The ways in are Snail.Sdk's; what is this server's are the words a model asks for its tools by. A program calls a tool
/// by its name without the server's prefix — <c>connector.find({ … })</c> for <c>connector_find</c> — and every command it
/// sends is the tool itself: held to the session's grant, checked by the same rules and written into the same journal.
/// </remarks>
internal static class ConnectorDiscovery
{
    /// <summary>The prefix every tool of this server shares; its word ranks nothing, since every tool has it.</summary>
    public const string Prefix = $"{ConnectorSettings.Name}_";

    /// <summary>
    /// The words a caller reaches for that a tool is not named with and its description need not carry. They live here rather
    /// than in the descriptions because a description is read by every session whether or not anyone searches, and these
    /// only when someone does; a guard test keeps them the tools that exist.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Meanings = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["connector_sources"] = "databases list available connections systems crm erp ledger which data company what there",
        ["connector_schema"] = "structure fields columns types indexes tables collections describe shape layout model",
        ["connector_find"] = "search look up documents records filter mongo mongodb get read show rows",
        ["connector_aggregate"] = "group sum total average pipeline analyse analysis statistics report top revenue per mongo mongodb join lookup",
        ["connector_count"] = "how many number total size mongo mongodb",
        ["connector_query"] = "sql select postgres postgresql mssql tsql rows join group sum report table",
        ["connector_export"] = "file csv excel xlsx spreadsheet download save person table",
        ["connector_journal"] = "history audit log queries asked before already earlier",
        ["connector_propose_write"] = "change update modify insert delete fix set edit correct",
        ["connector_apply"] = "run approved execute perform confirmed allowed",
        ["connector_diagnose"] = "health vault configured broken working check why version",
        ["find_tool"] = "search discover name parameters arguments how which",
        ["run_tools"] = "several many steps batch sequence list once",
        ["run_program"] = "program javascript loop decide script code calls"
    };

    /// <summary>How <c>find_tool</c> reads this server: its prefix, the words of each tool and the command a program calls it by.</summary>
    public static void Configure(DiscoveryOptions options)
    {
        options.Prefix = Prefix;
        options.CommandOf = CommandOf;

        foreach (var (tool, words) in Meanings)
            options.Meanings[tool] = words;
    }

    /// <summary>The command a sequence or a program sends for a tool, or null for the two that forward to the others.</summary>
    /// <remarks>As Snail.Sdk's <c>ToolCommands</c> names them: <c>connector_find</c> is <c>find</c>, any tool without the prefix keeps its name.</remarks>
    public static string? CommandOf(string tool) =>
        tool switch
        {
            "run_tools" or "run_program" => null,
            _ when tool.StartsWith(Prefix, StringComparison.Ordinal) => tool[Prefix.Length..],
            _ => tool
        };
}
