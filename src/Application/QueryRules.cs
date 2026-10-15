using System.Text.Json;
using System.Text.Json.Nodes;

namespace Snail.MCP.Connector.Application;

/// <summary>What a query may ask of a document database: read only, only the collections the source allows.</summary>
/// <remarks>
/// The read-only user is the wall; these rules are the door in front of it, so a query that would be refused is refused
/// with words a model can act on, before it reaches the database. A pipeline writes with <c>$out</c> and <c>$merge</c>
/// and runs code with <c>$function</c>, <c>$accumulator</c> and <c>$where</c> — none of which an analysis needs. A stage
/// that reads another collection — <c>$lookup</c>, <c>$graphLookup</c>, <c>$unionWith</c> — names one, and the source
/// has to allow it as it allows the first: a collection kept out by name must not come in through a join.
/// </remarks>
public static class QueryRules
{
    /// <summary>The operators no query of this server may hold, at any depth.</summary>
    public static readonly IReadOnlyList<string> Forbidden = ["$out", "$merge", "$function", "$accumulator", "$where"];

    private static readonly string[] Joining = ["$lookup", "$graphLookup", "$unionWith"];

    /// <summary>The filter, projection or sort as an object, or why it is not one this server runs.</summary>
    public static Answer<JsonObject> Document(string? text, string what)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Answer.Ok(new JsonObject());

        if (Parsed(text) is not JsonObject document)
            return Answer.Failed<JsonObject>(Messages.NotAnObject(what), Messages.ExtendedJsonHint);

        return Refused(document) is { } refused ? Answer.Failed<JsonObject>(refused, Messages.ReadOnlyHint) : Answer.Ok(document);
    }

    /// <summary>The pipeline as its stages, or why it is not one this server runs against the source.</summary>
    public static Answer<JsonArray> Pipeline(string? text, Source source)
    {
        if (Parsed(text ?? string.Empty) is not JsonArray stages || stages.Count == 0 || stages.Any(stage => stage is not JsonObject))
            return Answer.Failed<JsonArray>(Messages.NotAPipeline, Messages.ExtendedJsonHint);

        if (Refused(stages) is { } refused)
            return Answer.Failed<JsonArray>(refused, Messages.ReadOnlyHint);

        IReadOnlyList<string> joined = [.. stages.OfType<JsonObject>().SelectMany(Joined)];
        string? kept = joined.FirstOrDefault(collection => !source.Allows(collection));

        return kept is null ? Answer.Ok(stages) : Answer.Failed<JsonArray>(Messages.CollectionKept(source.Name, kept), Messages.CollectionsHint(source));
    }

    /// <summary>The first forbidden operator the node holds at any depth, as a refusal; null when it holds none.</summary>
    private static string? Refused(JsonNode? node) => node switch
    {
        JsonObject document => document.Select(pair => Forbidden.Contains(pair.Key, StringComparer.Ordinal) ? Messages.Forbidden(pair.Key) : Refused(pair.Value)).FirstOrDefault(refusal => refusal is not null),
        JsonArray items => items.Select(Refused).FirstOrDefault(refusal => refusal is not null),
        _ => null
    };

    /// <summary>The collections a stage reads besides the one the pipeline runs on, its own sub-pipelines included.</summary>
    private static IEnumerable<string> Joined(JsonObject stage)
    {
        foreach (var pair in stage)
        {
            if (!Joining.Contains(pair.Key, StringComparer.Ordinal))
                continue;

            if (pair.Value is JsonValue single && single.TryGetValue(out string? collection))
                yield return collection;

            if (pair.Value is not JsonObject join)
                continue;

            if ((join["from"] ?? join["coll"]) is { } from && from.GetValueKind() is JsonValueKind.String)
                yield return from.GetValue<string>();

            foreach (var inner in (join["pipeline"] as JsonArray ?? []).OfType<JsonObject>().SelectMany(Joined))
                yield return inner;
        }
    }

    private static JsonNode? Parsed(string text)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
