using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Snail.MCP.Connector.Application;
using Microsoft.Extensions.Options;
using Snail.MCP.Connector.Application.Ports;
using Snail.MCP.Connector.Configuration;

namespace Snail.MCP.Connector.Adapters;

/// <summary>MongoDB read by the source's read-only user, from a secondary when the replica set has one.</summary>
/// <remarks>
/// A client per connection string, kept for the life of the server: a client holds a pool and a monitor of the cluster,
/// and one made per query would open both every time. Reads prefer a secondary, so an agent's analysis does not load the
/// primary the company's own applications write to. Every query carries the server's time limit, which MongoDB enforces
/// itself (maxTimeMS). Rows are read one more than asked, so an answer knows it was cut.
/// </remarks>
public sealed partial class MongoDocuments(IOptions<ConnectorOptions> options) : IDocuments, IDisposable
{
    /// <summary>How long one call to the database may run, from QUERY_SECONDS.</summary>
    private int Seconds => options.Value.QuerySeconds;

    private static readonly JsonWriterSettings Relaxed = new() { OutputMode = JsonOutputMode.RelaxedExtendedJson };

    private const int Depth = 4;

    private const int ExampleLength = 60;

    private readonly ConcurrentDictionary<string, MongoClient> _clients = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, MongoClient> _writers = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public async Task<Answer<IReadOnlyList<string>>> CollectionsAsync(ReachableSource source, CancellationToken cancellationToken) =>
        await GuardedAsync(source, async () =>
        {
            var names = await Database(source).ListCollectionNamesAsync(cancellationToken: cancellationToken);

            return (IReadOnlyList<string>)await names.ToListAsync(cancellationToken);
        }, Seconds);

    /// <inheritdoc />
    public async Task<Answer<CollectionShape>> ShapeAsync(ReachableSource source, string collection, int sample, CancellationToken cancellationToken) =>
        await GuardedAsync(source, async () =>
        {
            var documents = Database(source).GetCollection<BsonDocument>(collection);
            long estimated = await documents.EstimatedDocumentCountAsync(cancellationToken: cancellationToken);
            var sampled = await documents.Aggregate(new AggregateOptions { MaxTime = TimeSpan.FromSeconds(Seconds) }).Sample(sample).ToListAsync(cancellationToken);
            var indexes = await (await documents.Indexes.ListAsync(cancellationToken)).ToListAsync(cancellationToken);

            return new CollectionShape(collection, estimated, sampled.Count, Fields(sampled)) { Indexes = [.. indexes.Select(index => index["key"].ToJson(Relaxed))] };
        }, Seconds);

    /// <inheritdoc />
    public async Task<Answer<RowPage>> FindAsync(ReachableSource source, DocumentQuery query, CancellationToken cancellationToken) =>
        await GuardedAsync(source, async () =>
        {
            var found = Database(source).GetCollection<BsonDocument>(query.Collection)
                .Find(BsonDocument.Parse(query.Filter), new FindOptions { MaxTime = TimeSpan.FromSeconds(query.Limit.Seconds) })
                .Limit(query.Limit.Rows + 1);

            if (query.Projection is { } projection)
                found = found.Project<BsonDocument>(BsonDocument.Parse(projection));

            if (query.Sort is { } sort)
                found = found.Sort(BsonDocument.Parse(sort));

            using var cursor = await found.ToCursorAsync(cancellationToken);

            return await PageAsync(cursor, query.Limit, cancellationToken);
        }, query.Limit.Seconds);

    /// <inheritdoc />
    public async Task<Answer<RowPage>> AggregateAsync(ReachableSource source, string collection, string pipeline, RowLimit limit, CancellationToken cancellationToken) =>
        await GuardedAsync(source, async () =>
        {
            var options = new AggregateOptions { MaxTime = TimeSpan.FromSeconds(limit.Seconds), AllowDiskUse = false };

            using var cursor = await Database(source).GetCollection<BsonDocument>(collection).AggregateAsync<BsonDocument>(Stages(pipeline, limit.Rows), options, cancellationToken);

            return await PageAsync(cursor, limit, cancellationToken);
        }, limit.Seconds);

    /// <inheritdoc />
    public async Task<Answer<long>> CountAsync(ReachableSource source, string collection, string filter, CancellationToken cancellationToken) =>
        await GuardedAsync(source, () =>
            Database(source).GetCollection<BsonDocument>(collection).CountDocumentsAsync(BsonDocument.Parse(filter), new CountOptions { MaxTime = TimeSpan.FromSeconds(Seconds) }, cancellationToken), Seconds);

    /// <inheritdoc />
    public async Task<Answer<WritePreview>> PreviewAsync(ReachableSource source, string collection, string filter, int sample, CancellationToken cancellationToken) =>
        await GuardedAsync(source, async () =>
        {
            var documents = Database(source).GetCollection<BsonDocument>(collection);
            var matching = BsonDocument.Parse(filter);
            long matched = await documents.CountDocumentsAsync(matching, new CountOptions { MaxTime = TimeSpan.FromSeconds(Seconds) }, cancellationToken);
            var shown = await documents.Find(matching, new FindOptions { MaxTime = TimeSpan.FromSeconds(Seconds) }).Limit(sample).ToListAsync(cancellationToken);

            return new WritePreview(matched, [.. shown.Select(Row)]);
        }, Seconds);

    /// <inheritdoc />
    /// <remarks>By the writing user, on the primary — a write never goes anywhere else — and only what the proposal says.</remarks>
    public async Task<Answer<long>> WriteAsync(ReachableSource source, WriteProposal proposal, CancellationToken cancellationToken)
    {
        if (source.Access.WriteUri is not { } uri)
            return Answer.Failed<long>(Messages.NoWriter, Messages.NoWriterHint);

        var documents = _writers.GetOrAdd(uri, Writer).GetDatabase(source.Source.Database).GetCollection<BsonDocument>(proposal.Collection);
        if (proposal.Operation is WriteOperation.Insert)
            return await GuardedAsync(source, () => InsertAsync(documents, proposal.Change, cancellationToken), Seconds);

        var matching = await GuardedAsync(source, () => MatchingAsync(documents, proposal, cancellationToken), Seconds);
        if (matching.Value is not { } ids)
            return Answer.Failed<long>(matching.Error!, matching.Hint);

        if (ids.Count > proposal.Matched)
            return Answer.Failed<long>(Messages.Changed(ids.Count, proposal.Matched));

        var bounded = Builders<BsonDocument>.Filter.And(BsonDocument.Parse(proposal.Filter), Builders<BsonDocument>.Filter.In("_id", ids));

        return await GuardedAsync(source, async () => proposal.Operation switch
        {
            WriteOperation.Update => (await documents.UpdateManyAsync(bounded, BsonDocument.Parse(proposal.Change), cancellationToken: cancellationToken)).ModifiedCount,
            WriteOperation.UpdateOne => (await documents.UpdateOneAsync(bounded, BsonDocument.Parse(proposal.Change), cancellationToken: cancellationToken)).ModifiedCount,
            _ => (await documents.DeleteManyAsync(bounded, cancellationToken)).DeletedCount
        }, Seconds);
    }

    /// <summary>The ids of the documents the proposal's filter matches now, one past what the person allowed at most.</summary>
    /// <remarks>
    /// A person allowed the write for the documents they read. Read again by the writing user, the filter may match more
    /// than then — the data grew — and the write is refused; otherwise it runs on exactly these ids, so a document that
    /// came in after the decision is not touched. MongoDB without a replica set has no transaction to roll back.
    /// </remarks>
    private async Task<IReadOnlyList<BsonValue>> MatchingAsync(IMongoCollection<BsonDocument> documents, WriteProposal proposal, CancellationToken cancellationToken)
    {
        int most = (int)Math.Min(int.MaxValue - 1, proposal.Matched) + 1;
        var found = await documents.Find(BsonDocument.Parse(proposal.Filter), new FindOptions { MaxTime = TimeSpan.FromSeconds(Seconds) })
            .Project(Builders<BsonDocument>.Projection.Include("_id")).Limit(most).ToListAsync(cancellationToken);

        return [.. found.Select(document => document["_id"])];
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> StreamAsync(ReachableSource source, DocumentQuery query, string? pipeline, int most, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var documents = Database(source).GetCollection<BsonDocument>(query.Collection);
        var time = TimeSpan.FromMinutes(10);

        using var cursor = pipeline is null
            ? await documents.Find(BsonDocument.Parse(query.Filter), new FindOptions { MaxTime = time }).Limit(most + 1).ToCursorAsync(cancellationToken)
            : await documents.AggregateAsync<BsonDocument>(Stages(pipeline, most), new AggregateOptions { MaxTime = time }, cancellationToken);

        while (await cursor.MoveNextAsync(cancellationToken))
        {
            foreach (var document in cursor.Current)
                yield return Row(document);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var client in _clients.Values.Concat(_writers.Values))
            client.Dispose();
    }

    private static async Task<long> InsertAsync(IMongoCollection<BsonDocument> documents, string change, CancellationToken cancellationToken)
    {
        BsonDocument[] added = [.. BsonSerializer.Deserialize<BsonArray>(change).Select(document => document.AsBsonDocument)];
        await documents.InsertManyAsync(added, cancellationToken: cancellationToken);

        return added.Length;
    }

    private static MongoClient Writer(string uri)
    {
        var settings = MongoClientSettings.FromConnectionString(uri);
        settings.ReadPreference = ReadPreference.Primary;
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(10);
        settings.ConnectTimeout = TimeSpan.FromSeconds(10);
        settings.ApplicationName = "snail-mcp-connector-writer";

        return new MongoClient(settings);
    }

    /// <summary>A document as one compact line of relaxed Extended JSON, its fractions written the shortest way that reads back the same.</summary>
    /// <remarks>
    /// The driver writes a double with seventeen digits — a revenue of 7533.7 came out as 7533.6999999999998, which a model
    /// repeats to a person as it is. Read back and written again, the same number is 7533.7, and the line loses the spaces
    /// the driver puts around every colon, so more rows fit in an answer's bytes.
    /// </remarks>
    public static string Row(BsonDocument document) => Tidied(JsonNode.Parse(document.ToJson(Relaxed)))?.ToJsonString() ?? "{}";

    private static JsonNode? Tidied(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject document:
                foreach (string key in document.Select(pair => pair.Key).ToList())
                    document[key] = Tidied(document[key]?.DeepClone());

                return document;
            case JsonArray items:
                for (int index = 0; index < items.Count; index++)
                    items[index] = Tidied(items[index]?.DeepClone());

                return items;
            case JsonValue value when value.GetValueKind() is JsonValueKind.Number && !value.TryGetValue(out long _) && value.TryGetValue(out double fraction):
                return JsonValue.Create(fraction);
            default:
                return node;
        }
    }

    /// <summary>The message of a failure without the credentials a connection string may carry.</summary>
    public static string Scrubbed(string message) => Credentials().Replace(message, "://***@");

    /// <summary>The pipeline's stages with a limit one past what is asked, so the answer knows when it was cut.</summary>
    private static PipelineDefinition<BsonDocument, BsonDocument> Stages(string pipeline, int rows)
    {
        BsonDocument[] stages = [.. BsonSerializer.Deserialize<BsonArray>(pipeline).Select(stage => stage.AsBsonDocument), new BsonDocument("$limit", rows + 1)];

        return PipelineDefinition<BsonDocument, BsonDocument>.Create(stages);
    }

    private IMongoDatabase Database(ReachableSource source) =>
        _clients.GetOrAdd(source.Access.ReadUri, Client).GetDatabase(source.Source.Database);

    private static MongoClient Client(string uri)
    {
        var settings = MongoClientSettings.FromConnectionString(uri);
        settings.ReadPreference = ReadPreference.SecondaryPreferred;
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(10);
        settings.ConnectTimeout = TimeSpan.FromSeconds(10);
        settings.ApplicationName = "snail-mcp-connector";

        return new MongoClient(settings);
    }

    private static async Task<RowPage> PageAsync(IAsyncCursor<BsonDocument> cursor, RowLimit limit, CancellationToken cancellationToken)
    {
        List<string> rows = [];
        int bytes = 0;

        while (await cursor.MoveNextAsync(cancellationToken))
        {
            foreach (var document in cursor.Current)
            {
                string row = Row(document);
                bytes += Encoding.UTF8.GetByteCount(row);

                if (rows.Count == limit.Rows || bytes > limit.Bytes)
                    return new RowPage(rows, IsCut: true);

                rows.Add(row);
            }
        }

        return new RowPage(rows, IsCut: false);
    }

    private static async Task<Answer<T>> GuardedAsync<T>(ReachableSource source, Func<Task<T>> read, int seconds)
    {
        try
        {
            return Answer.Ok(await read());
        }
        catch (MongoExecutionTimeoutException)
        {
            return Answer.Failed<T>(Messages.TooSlow(seconds), Messages.TooSlowHint);
        }
        catch (Exception exception) when (exception is MongoException or TimeoutException or FormatException or BsonException or InvalidOperationException)
        {
            return Answer.Failed<T>(Messages.DatabaseSaid(source.Source.Name, Scrubbed(exception.Message)), Messages.ExtendedJsonHint);
        }
    }

    private static IReadOnlyList<FieldShape> Fields(IReadOnlyList<BsonDocument> documents)
    {
        var seen = new Dictionary<string, FieldTally>(StringComparer.Ordinal);

        foreach (var document in documents)
        {
            var present = new HashSet<string>(StringComparer.Ordinal);
            Walk(document, string.Empty, 0, seen, present);
        }

        return [.. seen.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value.Shape(pair.Key, documents.Count))];
    }

    private static void Walk(BsonDocument document, string prefix, int depth, Dictionary<string, FieldTally> seen, HashSet<string> present)
    {
        foreach (var element in document)
        {
            string path = prefix.Length == 0 ? element.Name : $"{prefix}.{element.Name}";
            Count(path, element.Value, depth, seen, present);
        }
    }

    private static void Count(string path, BsonValue value, int depth, Dictionary<string, FieldTally> seen, HashSet<string> present)
    {
        var tally = seen.TryGetValue(path, out var known) ? known : seen[path] = new FieldTally();
        tally.Add(TypeOf(value), present.Add(path), value);

        if (depth >= Depth)
            return;

        if (value is BsonDocument inner)
            Walk(inner, path, depth + 1, seen, present);

        if (value is BsonArray items)
        {
            foreach (var item in items.Take(20))
                Count($"{path}[]", item, depth + 1, seen, present);
        }
    }

    private static string TypeOf(BsonValue value) => value.BsonType switch
    {
        BsonType.String => "string",
        BsonType.Int32 or BsonType.Int64 => "int",
        BsonType.Double => "double",
        BsonType.Decimal128 => "decimal",
        BsonType.Boolean => "bool",
        BsonType.DateTime or BsonType.Timestamp => "date",
        BsonType.ObjectId => "objectId",
        BsonType.Document => "object",
        BsonType.Array => "array",
        BsonType.Null or BsonType.Undefined => "null",
        _ => value.BsonType.ToString().ToLowerInvariant()
    };

    [System.Text.RegularExpressions.GeneratedRegex(@"://[^/@\s]+@")]
    private static partial System.Text.RegularExpressions.Regex Credentials();

    /// <summary>What the sample said about one field so far.</summary>
    private sealed class FieldTally
    {
        private readonly Dictionary<string, int> _types = new(StringComparer.Ordinal);

        private int _documents;

        private string? _example;

        public void Add(string type, bool isFirstInDocument, BsonValue value)
        {
            _types[type] = _types.GetValueOrDefault(type) + 1;

            if (isFirstInDocument)
                _documents++;

            if (_example is null && value.BsonType is not (BsonType.Document or BsonType.Array or BsonType.Null))
                _example = Shortened(value.ToJson(Relaxed));
        }

        public FieldShape Shape(string path, int sampled) =>
            new(path, [.. _types.OrderByDescending(pair => pair.Value).Select(pair => pair.Key)], sampled == 0 ? 0 : Math.Round((double)_documents / sampled, 2), _example);

        private static string Shortened(string text) => text.Length <= ExampleLength ? text : $"{text[..ExampleLength]}…";
    }
}
