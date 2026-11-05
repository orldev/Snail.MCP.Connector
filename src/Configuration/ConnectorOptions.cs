namespace Snail.MCP.Connector.Configuration;

/// <summary>Where the sources are declared, what one answer may carry and how long a query may take.</summary>
public sealed class ConnectorOptions
{
    /// <summary>Vault's HTTP address the sources are read from; empty leaves the server with no sources.</summary>
    public string VaultAddress { get; set; } = string.Empty;

    /// <summary>The token the server reads Vault with: read and list under <see cref="VaultPath"/> and nothing else.</summary>
    public string VaultToken { get; set; } = string.Empty;

    /// <summary>The KV v2 mount the sources live under.</summary>
    public string VaultMount { get; set; } = "secret";

    /// <summary>The folder of the mount that holds one secret per source, named as the source.</summary>
    public string VaultPath { get; set; } = "connectors";

    /// <summary>Folder the platform's working copies lie under, as this server sees them: exports are written there.</summary>
    public string WorkspacesRoot { get; set; } = "/workspaces";

    /// <summary>Rows one answer carries at most; a query asking for more is cut and says so.</summary>
    public int MostRows { get; set; } = 200;

    /// <summary>Rows one answer carries when the query names no limit.</summary>
    public int Rows { get; set; } = 50;

    /// <summary>Bytes of rows one answer carries at most, so a page of wide documents does not fill a model's window.</summary>
    public int MostBytes { get; set; } = 65536;

    /// <summary>Rows an export writes at most.</summary>
    public int MostExportRows { get; set; } = 100000;

    /// <summary>Seconds the database gets to stream the rows of one export.</summary>
    public int ExportSeconds { get; set; } = 600;

    /// <summary>Columns an export writes at most; the fields past them go into one column as the JSON of the rest.</summary>
    public int MostExportColumns { get; set; } = 1000;

    /// <summary>Documents a collection's shape is read from.</summary>
    public int Sample { get; set; } = 200;

    /// <summary>Seconds the database gets for one query before it is stopped.</summary>
    public int QuerySeconds { get; set; } = 30;

    /// <summary>Days the journal of queries keeps.</summary>
    public int JournalDays { get; set; } = 365;

    /// <summary>The key the stand's dashboard shows a proposal and gives its decision with; empty keeps writing off.</summary>
    /// <remarks>The agent never sees it: it is how the server tells a person's decision from anything a model could send.</remarks>
    public string ApprovalKey { get; set; } = string.Empty;

    /// <summary>Documents one write may touch at most; a proposal matching more is refused, to be narrowed.</summary>
    public int MostWritten { get; set; } = 1000;

    /// <summary>Documents of the matched ones a proposal shows a person.</summary>
    public int ProposalSample { get; set; } = 3;

    /// <summary>Days a proposal waits for a decision before it can no longer be applied.</summary>
    public int ProposalDays { get; set; } = 7;
}
