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

    /// <summary>How an agent's JSON is read: a key written twice is refused, so no reader of it can keep the other one.</summary>
    /// <remarks>Read leniently, a <c>$lookup</c> with its <c>from</c> written twice threw an ArgumentException past every rule (audit of 30.10.2026).</remarks>
    internal static readonly JsonDocumentOptions Strict = new() { AllowDuplicateProperties = false };

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

        IReadOnlyList<string> joined = [.. Joined(stages)];
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

    /// <summary>The collections a node reads besides the one the pipeline runs on, at any depth.</summary>
    /// <remarks>
    /// The whole stage is walked, not its top: a join inside <c>$facet</c>, inside another join's pipeline or anywhere else
    /// a sub-pipeline can stand names its collection too. A join into another database — <c>from</c> written as an
    /// object — is answered by its JSON, which no source allows, so it is refused by name.
    /// </remarks>
    private static IEnumerable<string> Joined(JsonNode? node)
    {
        if (node is JsonArray items)
        {
            foreach (var inner in items.SelectMany(Joined))
                yield return inner;

            yield break;
        }

        if (node is not JsonObject document)
            yield break;

        foreach (var pair in document)
        {
            if (Joining.Contains(pair.Key, StringComparer.Ordinal))
            {
                foreach (var collection in Collections(pair.Value))
                    yield return collection;
            }

            foreach (var inner in Joined(pair.Value))
                yield return inner;
        }
    }

    /// <summary>The collection a joining stage names: its value when it is one name, its from or coll otherwise.</summary>
    private static IEnumerable<string> Collections(JsonNode? join)
    {
        if (join is JsonValue single && single.TryGetValue(out string? collection))
            yield return collection;

        if (join is not JsonObject stage)
            yield break;

        if ((stage["from"] ?? stage["coll"]) is { } from)
            yield return from.GetValueKind() is JsonValueKind.String ? from.GetValue<string>() : from.ToJsonString();
    }

    private static JsonNode? Parsed(string text)
    {
        try
        {
            return JsonNode.Parse(text, documentOptions: Strict);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
