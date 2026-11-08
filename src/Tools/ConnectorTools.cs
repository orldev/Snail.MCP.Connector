using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Snail.MCP.Connector.Application;
using Snail.Sdk.Mcp.Hosting.Jobs;

namespace Snail.MCP.Connector.Tools;

/// <summary>The company's databases, read for the task: the sources, their shape, their rows, their exports and the journal of what was asked.</summary>
[McpServerToolType]
public sealed class ConnectorTools(Queries queries, Jobs jobs)
{
    /// <summary>Lists the sources.</summary>
    [McpServerTool(Name = "connector_sources", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(ToolDescriptions.Sources)]
    public async Task<CallToolResult> SourcesAsync(CancellationToken cancellationToken = default) =>
        ConnectorAnswers.From(await queries.SourcesAsync(cancellationToken));

    /// <summary>Reads a source's collections, or one collection's shape.</summary>
    [McpServerTool(Name = "connector_schema", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(ToolDescriptions.Schema)]
    public async Task<CallToolResult> SchemaAsync(
        [Description(ToolDescriptions.SourceName)] string source,
        [Description(ToolDescriptions.SchemaCollection)] string? collection = null,
        CancellationToken cancellationToken = default) =>
        string.IsNullOrWhiteSpace(collection)
            ? ConnectorAnswers.From(await queries.CollectionsAsync(source, cancellationToken))
            : ConnectorAnswers.From(await queries.ShapeAsync(source, collection, cancellationToken));

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
        ConnectorAnswers.From(await queries.FindAsync(source, collection, filter, projection, sort, limit, cancellationToken));

    /// <summary>Runs an aggregation pipeline.</summary>
    [McpServerTool(Name = "connector_aggregate", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(ToolDescriptions.Aggregate)]
    public async Task<CallToolResult> AggregateAsync(
        [Description(ToolDescriptions.SourceName)] string source,
        [Description(ToolDescriptions.Collection)] string collection,
        [Description(ToolDescriptions.Pipeline)] string pipeline,
        [Description(ToolDescriptions.Limit)] int? limit = null,
        CancellationToken cancellationToken = default) =>
        ConnectorAnswers.From(await queries.AggregateAsync(source, collection, pipeline, limit, cancellationToken));

    /// <summary>Counts the documents a filter matches.</summary>
    [McpServerTool(Name = "connector_count", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(ToolDescriptions.Count)]
    public async Task<CallToolResult> CountAsync(
        [Description(ToolDescriptions.SourceName)] string source,
        [Description(ToolDescriptions.Collection)] string collection,
        [Description(ToolDescriptions.Filter)] string? filter = null,
        CancellationToken cancellationToken = default) =>
        ConnectorAnswers.From(await queries.CountAsync(source, collection, filter, cancellationToken));

    /// <summary>Runs a SELECT on a SQL source.</summary>
    [McpServerTool(Name = "connector_query", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(ToolDescriptions.Query)]
    public async Task<CallToolResult> QueryAsync(
        [Description(ToolDescriptions.SourceName)] string source,
        [Description(ToolDescriptions.Select)] string select,
        [Description(ToolDescriptions.Limit)] int? limit = null,
        CancellationToken cancellationToken = default) =>
        ConnectorAnswers.From(await queries.QueryAsync(source, select, limit, cancellationToken));

    /// <summary>Writes rows into a table file of the task, answered within the wait or as the job writing it.</summary>
    /// <remarks>
    /// The export is checked and begun in the journal while the call is served; its rows are written by a job of the
    /// task, which a client that can wait follows through the job's resource, so no model is made to ask again. A call
    /// given up stops the waiting, not the export: the file still lands in the copy, and the journal says so.
    /// </remarks>
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
        [Description(ToolDescriptions.ExportWait)] int wait = 60,
        CancellationToken cancellationToken = default)
    {
        if (wait is < 0 or > ToolDescriptions.LongestWait)
            return ToolAnswer.Failed(Messages.WaitOutside(wait, ToolDescriptions.LongestWait), Messages.WaitOutsideHint);

        var prepared = await queries.PrepareExportAsync(source, collection, filter, pipeline, select, format, file, cancellationToken);
        if (!prepared.TryGet(out var export))
            return ToolAnswer.Failed(prepared);

        var job = jobs.Start(Export, export.Task, async (_, token) => (await export.WriteAsync(token)).Map(ConnectorAnswers.Data), ToolAnswer.ObjectInfo);
        var report = await job.ForAsync(TimeSpan.FromSeconds(wait), cancellationToken);
        var running = new JsonObject { ["file"] = export.File, ["note"] = Messages.ExportRunning(export.File) };

        return JobAnswer.Result(report, running, Messages.ExportAgainHint);
    }

    /// <summary>The kind of an export's job, the middle of its URI: <c>job://connector/export/&lt;id&gt;</c>.</summary>
    public const string Export = "export";

    /// <summary>Reads the queries this task made.</summary>
    [McpServerTool(Name = "connector_journal", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(ToolDescriptions.Journal)]
    public async Task<CallToolResult> JournalAsync(
        [Description(ToolDescriptions.JournalMost)] int most = 20,
        CancellationToken cancellationToken = default) =>
        ConnectorAnswers.From(await queries.JournalAsync(most, cancellationToken));
}
