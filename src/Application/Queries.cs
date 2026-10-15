using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Snail.MCP.Connector.Application.Ports;
using Snail.MCP.Connector.Configuration;

namespace Snail.MCP.Connector.Application;

/// <summary>What the tools ask of the company databases: the sources, their shape, their rows and their exports, each written into the journal.</summary>
/// <remarks>
/// Every query is checked against <see cref="QueryRules"/> and the source's collections before it reaches the database,
/// goes through the read-only user, and is written into the journal under the session's task whether it succeeded or
/// not. An answer whose query could not be written down is withheld: the owner's rule is that no data of a company
/// reaches an agent unrecorded.
/// </remarks>
public sealed partial class Queries(
    ISources sources,
    IDocuments documents,
    IQueryJournal journal,
    ITableFiles files,
    SessionCopy copy,
    TimeProvider clock,
    IOptions<ConnectorOptions> options)
{
    /// <summary>Every source declared on the stand.</summary>
    public Task<Answer<IReadOnlyList<Source>>> SourcesAsync(CancellationToken cancellationToken) => sources.ListAsync(cancellationToken);

    /// <summary>The collections of a source an agent may read, among the ones that exist.</summary>
    public async Task<Answer<SourceCollections>> CollectionsAsync(string source, CancellationToken cancellationToken)
    {
        var reached = await ReachAsync(source, null, cancellationToken);
        if (reached.Value is not { } context)
            return Answer.Failed<SourceCollections>(reached.Error!, reached.Hint);

        var listed = await RecordedAsync(context, Source.Every, "schema", "{}", () => documents.CollectionsAsync(context.Source, cancellationToken), names => names.Count, cancellationToken);
        if (listed.Value is not { } names)
            return Answer.Failed<SourceCollections>(listed.Error!, listed.Hint);

        return Answer.Ok(new SourceCollections(context.Source.Source, [.. names.Where(context.Source.Source.Allows).Order(StringComparer.Ordinal)]));
    }

    /// <summary>The shape of one collection, read from a sample of its documents.</summary>
    public async Task<Answer<CollectionShape>> ShapeAsync(string source, string collection, CancellationToken cancellationToken)
    {
        var reached = await ReachAsync(source, collection, cancellationToken);
        if (reached.Value is not { } context)
            return Answer.Failed<CollectionShape>(reached.Error!, reached.Hint);

        return await RecordedAsync(context, collection, "schema", $$"""{"sample":{{options.Value.Sample}}}""",
            () => documents.ShapeAsync(context.Source, collection, options.Value.Sample, cancellationToken), shape => shape.Sampled, cancellationToken);
    }

    /// <summary>The documents a filter matches.</summary>
    public async Task<Answer<RowPage>> FindAsync(string source, string collection, string? filter, string? projection, string? sort, int? limit, CancellationToken cancellationToken)
    {
        var reached = await ReachAsync(source, collection, cancellationToken);
        if (reached.Value is not { } context)
            return Answer.Failed<RowPage>(reached.Error!, reached.Hint);

        var parts = new[] { QueryRules.Document(filter, "filter"), QueryRules.Document(projection, "projection"), QueryRules.Document(sort, "sort") };
        if (parts.FirstOrDefault(part => part.Value is null) is { } refused)
            return Answer.Failed<RowPage>(refused.Error!, refused.Hint);

        var query = new DocumentQuery(collection, parts[0].Value!.ToJsonString(), Optional(projection, parts[1]), Optional(sort, parts[2]), Limit(limit));
        string recorded = new JsonObject { ["filter"] = parts[0].Value!.DeepClone(), ["projection"] = parts[1].Value!.DeepClone(), ["sort"] = parts[2].Value!.DeepClone(), ["limit"] = query.Limit.Rows }.ToJsonString();

        return await RecordedAsync(context, collection, "find", recorded, () => documents.FindAsync(context.Source, query, cancellationToken), page => page.Rows.Count, cancellationToken);
    }

    /// <summary>What an aggregation pipeline answers.</summary>
    public async Task<Answer<RowPage>> AggregateAsync(string source, string collection, string pipeline, int? limit, CancellationToken cancellationToken)
    {
        var reached = await ReachAsync(source, collection, cancellationToken);
        if (reached.Value is not { } context)
            return Answer.Failed<RowPage>(reached.Error!, reached.Hint);

        var stages = QueryRules.Pipeline(pipeline, context.Source.Source);
        if (stages.Value is null)
            return Answer.Failed<RowPage>(stages.Error!, stages.Hint);

        string text = stages.Value.ToJsonString();

        return await RecordedAsync(context, collection, "aggregate", text, () => documents.AggregateAsync(context.Source, collection, text, Limit(limit), cancellationToken), page => page.Rows.Count, cancellationToken);
    }

    /// <summary>How many documents a filter matches.</summary>
    public async Task<Answer<long>> CountAsync(string source, string collection, string? filter, CancellationToken cancellationToken)
    {
        var reached = await ReachAsync(source, collection, cancellationToken);
        if (reached.Value is not { } context)
            return Answer.Failed<long>(reached.Error!, reached.Hint);

        var matched = QueryRules.Document(filter, "filter");
        if (matched.Value is null)
            return Answer.Failed<long>(matched.Error!, matched.Hint);

        string text = matched.Value.ToJsonString();

        return await RecordedAsync(context, collection, "count", text, () => documents.CountAsync(context.Source, collection, text, cancellationToken), count => count, cancellationToken);
    }

    /// <summary>Writes the rows of a filter or a pipeline into a CSV or Excel file in the task's working copy.</summary>
    public async Task<Answer<ExportedFile>> ExportAsync(string source, string collection, string? filter, string? pipeline, string format, string file, CancellationToken cancellationToken)
    {
        var reached = await ReachAsync(source, collection, cancellationToken);
        if (reached.Value is not { } context)
            return Answer.Failed<ExportedFile>(reached.Error!, reached.Hint);

        if (!Enum.TryParse(format, ignoreCase: true, out TableFormat table) || !Enum.IsDefined(table))
            return Answer.Failed<ExportedFile>(Messages.BadFormat);

        if (!FileName().IsMatch(file))
            return Answer.Failed<ExportedFile>(Messages.BadFileName);

        if (string.IsNullOrWhiteSpace(filter) == string.IsNullOrWhiteSpace(pipeline))
            return Answer.Failed<ExportedFile>(Messages.OneQuery);

        var checkedFilter = QueryRules.Document(filter, "filter");
        var checkedPipeline = pipeline is null ? Answer.Ok(new JsonArray()) : QueryRules.Pipeline(pipeline, context.Source.Source);
        if (checkedFilter.Value is null || checkedPipeline.Value is null)
            return Answer.Failed<ExportedFile>((checkedFilter.Error ?? checkedPipeline.Error)!, checkedFilter.Hint ?? checkedPipeline.Hint);

        var query = new DocumentQuery(collection, checkedFilter.Value.ToJsonString(), null, null, Limit(null));
        string? stages = pipeline is null ? null : checkedPipeline.Value.ToJsonString();
        string name = Path.HasExtension(file) ? file : $"{file}.{table.ToString().ToLowerInvariant()}";
        string recorded = new JsonObject { ["filter"] = checkedFilter.Value.DeepClone(), ["pipeline"] = checkedPipeline.Value.DeepClone(), ["file"] = name }.ToJsonString();
        var rows = documents.StreamAsync(context.Source, query, stages, options.Value.MostExportRows, cancellationToken);

        return await RecordedAsync(context, collection, "export", recorded,
            () => files.WriteAsync(context.Copy.Path, name, table, rows, options.Value.MostExportRows, cancellationToken), exported => exported.Rows, cancellationToken);
    }

    /// <summary>The newest queries of this session's task, for an agent to see what it already asked.</summary>
    public async Task<Answer<IReadOnlyList<QueryRecord>>> JournalAsync(int most, CancellationToken cancellationToken)
    {
        var place = copy.Current();
        if (place.Value is not { } current)
            return Answer.Failed<IReadOnlyList<QueryRecord>>(place.Error!, place.Hint);

        return Answer.Ok(await journal.ReadAsync(current.Task, Math.Clamp(most, 1, options.Value.MostRows), cancellationToken));
    }

    private async Task<Answer<QueryContext>> ReachAsync(string name, string? collection, CancellationToken cancellationToken)
    {
        var place = copy.Current();
        if (place.Value is not { } current)
            return Answer.Failed<QueryContext>(place.Error!, place.Hint);

        var found = await sources.FindAsync(name.Trim(), cancellationToken);
        if (found.Value is not { } source)
            return Answer.Failed<QueryContext>(found.Error!, found.Hint);

        if (source.Source.Kind is not SourceKind.MongoDb)
            return Answer.Failed<QueryContext>(Messages.NotDocuments);

        if (!string.IsNullOrWhiteSpace(collection) && !source.Source.Allows(collection))
            return Answer.Failed<QueryContext>(Messages.CollectionKept(source.Source.Name, collection), Messages.CollectionsHint(source.Source));

        return Answer.Ok(new QueryContext(source, current));
    }

    private async Task<Answer<T>> RecordedAsync<T>(QueryContext context, string collection, string operation, string query, Func<Task<Answer<T>>> run, Func<T, long> rows, CancellationToken cancellationToken)
    {
        long started = clock.GetTimestamp();
        var answer = await run();
        long milliseconds = (long)clock.GetElapsedTime(started).TotalMilliseconds;

        var record = new QueryRecord(clock.GetUtcNow(), context.Copy.Task, context.Source.Source.Name, collection, operation, query,
            answer.Value is { } value ? rows(value) : 0, milliseconds, answer.Error);

        var written = await journal.WriteAsync(record, cancellationToken);

        return written.Error is null ? answer : Answer.Failed<T>(Messages.JournalDown, Messages.JournalDownHint);
    }

    private RowLimit Limit(int? asked) =>
        new(Math.Clamp(asked ?? options.Value.Rows, 1, options.Value.MostRows), options.Value.MostBytes, options.Value.QuerySeconds);

    private static string? Optional(string? asked, Answer<JsonObject> parsed) => string.IsNullOrWhiteSpace(asked) ? null : parsed.Value!.ToJsonString();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$")]
    private static partial Regex FileName();

    /// <summary>The source a query goes to, and the copy of the task it is made for.</summary>
    private sealed record QueryContext(ReachableSource Source, CopyPlace Copy);
}
