using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Snail.MCP.Connector.Application;

namespace Snail.MCP.Connector.Tools;

/// <summary>The company's databases, read for the task: the sources, their shape, their rows, their exports and the journal of what was asked.</summary>
[McpServerToolType]
public sealed class ConnectorTools(Queries queries)
{
    /// <summary>Lists the sources.</summary>
    [McpServerTool(Name = "connector_sources", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(ToolDescriptions.Sources)]
    public async Task<CallToolResult> SourcesAsync(CancellationToken cancellationToken = default) =>
        ToolResponse.From(await queries.SourcesAsync(cancellationToken));

    /// <summary>Reads a source's collections, or one collection's shape.</summary>
    [McpServerTool(Name = "connector_schema", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(ToolDescriptions.Schema)]
    public async Task<CallToolResult> SchemaAsync(
        [Description(ToolDescriptions.SourceName)] string source,
        [Description(ToolDescriptions.SchemaCollection)] string? collection = null,
        CancellationToken cancellationToken = default) =>
        string.IsNullOrWhiteSpace(collection)
            ? ToolResponse.From(await queries.CollectionsAsync(source, cancellationToken))
            : ToolResponse.From(await queries.ShapeAsync(source, collection, cancellationToken));

    /// <summary>Reads the documents a filter matches.</summary>
    [McpServerTool(Name = "connector_find", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(ToolDescriptions.Find)]
    public async Task<CallToolResult> FindAsync(
        [Description(ToolDescriptions.SourceName)] string source,
        [Description(ToolDescriptions.Collection)] string collection,
        [Description(ToolDescriptions.Filter)] string? filter = null,
        [Description(ToolDescriptions.Projection)] string? projection = null,
        [Description(ToolDescriptions.Sort)] string? sort = null,
        [Description(ToolDescriptions.Limit)] int? limit = null,
        CancellationToken cancellationToken = default) =>
        ToolResponse.From(await queries.FindAsync(source, collection, filter, projection, sort, limit, cancellationToken));

    /// <summary>Runs an aggregation pipeline.</summary>
    [McpServerTool(Name = "connector_aggregate", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(ToolDescriptions.Aggregate)]
    public async Task<CallToolResult> AggregateAsync(
        [Description(ToolDescriptions.SourceName)] string source,
        [Description(ToolDescriptions.Collection)] string collection,
        [Description(ToolDescriptions.Pipeline)] string pipeline,
        [Description(ToolDescriptions.Limit)] int? limit = null,
        CancellationToken cancellationToken = default) =>
        ToolResponse.From(await queries.AggregateAsync(source, collection, pipeline, limit, cancellationToken));

    /// <summary>Counts the documents a filter matches.</summary>
    [McpServerTool(Name = "connector_count", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(ToolDescriptions.Count)]
    public async Task<CallToolResult> CountAsync(
        [Description(ToolDescriptions.SourceName)] string source,
        [Description(ToolDescriptions.Collection)] string collection,
        [Description(ToolDescriptions.Filter)] string? filter = null,
        CancellationToken cancellationToken = default) =>
        ToolResponse.From(await queries.CountAsync(source, collection, filter, cancellationToken));

    /// <summary>Runs a SELECT on a SQL source.</summary>
    [McpServerTool(Name = "connector_query", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(ToolDescriptions.Query)]
    public async Task<CallToolResult> QueryAsync(
        [Description(ToolDescriptions.SourceName)] string source,
        [Description(ToolDescriptions.Select)] string select,
        [Description(ToolDescriptions.Limit)] int? limit = null,
        CancellationToken cancellationToken = default) =>
        ToolResponse.From(await queries.QueryAsync(source, select, limit, cancellationToken));

    /// <summary>Writes rows into a table file of the task.</summary>
    [McpServerTool(Name = "connector_export", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(ToolDescriptions.Export)]
    public async Task<CallToolResult> ExportAsync(
        [Description(ToolDescriptions.SourceName)] string source,
        [Description(ToolDescriptions.Format)] string format,
        [Description(ToolDescriptions.File)] string file,
        [Description(ToolDescriptions.ExportCollection)] string? collection = null,
        [Description(ToolDescriptions.Filter)] string? filter = null,
        [Description(ToolDescriptions.ExportPipeline)] string? pipeline = null,
        [Description(ToolDescriptions.ExportSelect)] string? select = null,
        CancellationToken cancellationToken = default) =>
        ToolResponse.From(await queries.ExportAsync(source, collection, filter, pipeline, select, format, file, cancellationToken));

    /// <summary>Reads the queries this task made.</summary>
    [McpServerTool(Name = "connector_journal", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(ToolDescriptions.Journal)]
    public async Task<CallToolResult> JournalAsync(
        [Description(ToolDescriptions.JournalMost)] int most = 20,
        CancellationToken cancellationToken = default) =>
        ToolResponse.From(await queries.JournalAsync(most, cancellationToken));
}
