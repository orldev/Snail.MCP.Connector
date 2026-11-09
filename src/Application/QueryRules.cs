using System.Text.Json;
using System.Text.Json.Nodes;
using Snail.Sdk.Mcp.Hosting;

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
            return Answer.Failed(Messages.NotAnObject(what), Messages.ExtendedJsonHint, ToolFailures.Arguments);

        return Refused(document) is { } refused ? Answer.Failed(refused, Messages.ReadOnlyHint, FailureTypes.NotAllowed) : Answer.Ok(document);
    }

    /// <summary>The pipeline as its stages, or why it is not one this server runs against the source.</summary>
    public static Answer<JsonArray> Pipeline(string? text, Source source)
    {
        if (Parsed(text ?? string.Empty) is not JsonArray stages || stages.Count == 0 || stages.Any(stage => stage is not JsonObject))
            return Answer.Failed(Messages.NotAPipeline, Messages.ExtendedJsonHint, ToolFailures.Arguments);

        if (Refused(stages) is { } refused)
            return Answer.Failed(refused, Messages.ReadOnlyHint, FailureTypes.NotAllowed);

        IReadOnlyList<string> joined = Joined(stages);
        string? kept = joined.FirstOrDefault(collection => !source.Allows(collection));

        return kept is null ? Answer.Ok(stages) : Answer.Failed(Messages.CollectionKept(source.Name, kept), Messages.CollectionsHint(source), FailureTypes.NotAllowed);
    }

    /// <summary>The first forbidden operator the node holds at any depth, as a refusal; null when it holds none.</summary>
    private static string? Refused(JsonNode? node) => node switch
    {
        JsonObject document => document.Select(pair => Forbidden.Contains(pair.Key, StringComparer.Ordinal) ? Messages.Forbidden(pair.Key) : Refused(pair.Value)).FirstOrDefault(refusal => refusal is not null),
        JsonArray items => items.Select(Refused).FirstOrDefault(refusal => refusal is not null),
        _ => null
    };

    /// <summary>The collections a node reads besides the one the pipeline runs on, at any depth, in the order written.</summary>
    /// <remarks>
    /// The whole stage is walked, not its top: a join inside <c>$facet</c>, inside another join's pipeline or anywhere else
    /// a sub-pipeline can stand names its collection too. A join into another database — <c>from</c> written as an
    /// object — is answered by its JSON, which no source allows, so it is refused by name. Walked with one stack rather
    /// than an iterator per node, one inside another: a pipeline of two hundred stages and a join allocated 403 KB that
    /// way against 336 KB before the walk went deep (audit of 30.10.2026).
    /// </remarks>
    private static IReadOnlyList<string> Joined(JsonArray stages)
    {
        List<string> joined = [];
        var waiting = new Stack<(JsonNode? Node, bool IsJoin)>();
        waiting.Push((stages, IsJoin: false));

        while (waiting.TryPop(out var next))
        {
            if (next.IsJoin)
            {
                joined.AddRange(Collections(next.Node));
                continue;
            }

            if (next.Node is JsonArray items)
            {
                for (int index = items.Count - 1; index >= 0; index--)
                    waiting.Push((items[index], IsJoin: false));

                continue;
            }

            if (next.Node is not JsonObject document)
                continue;

            for (int index = document.Count - 1; index >= 0; index--)
            {
                var pair = document.GetAt(index);
                waiting.Push((pair.Value, IsJoin: false));

                if (Joining.Contains(pair.Key, StringComparer.Ordinal))
                    waiting.Push((pair.Value, IsJoin: true));
            }
        }

        return joined;
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
