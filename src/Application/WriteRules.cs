using System.Text.Json;
using System.Text.Json.Nodes;
using Snail.Sdk.Mcp.Hosting;

namespace Snail.MCP.Connector.Application;

/// <summary>What a proposed write may be: an update by field operators, an insert of documents, a delete — always narrowed by a filter.</summary>
/// <remarks>
/// An update or a delete with an empty filter would touch every document of the collection; a person reading «matched
/// 500 000» may still press yes, so it is refused before it reaches them. An update is field operators only — a
/// replacement document would drop every field it does not name — and none of the operators that run code inside the
/// database are let through here either.
/// </remarks>
public static class WriteRules
{
    /// <summary>The update operators a proposal may use.</summary>
    public static readonly IReadOnlyList<string> Operators =
        ["$set", "$unset", "$inc", "$mul", "$min", "$max", "$rename", "$currentDate", "$push", "$pull", "$addToSet", "$pop"];

    /// <summary>The operation a word names, or why it names none.</summary>
    public static Answer<WriteOperation> Operation(string word) =>
        Enum.TryParse(word.Replace("_", string.Empty, StringComparison.Ordinal), ignoreCase: true, out WriteOperation operation) && Enum.IsDefined(operation)
            ? Answer.Ok(operation)
            : Answer.Failed(Messages.BadOperation, type: ToolFailures.Arguments);

    /// <summary>The operation as a proposal is asked with and every answer and journal line spells it: update, update_one, insert, delete.</summary>
    public static string Word(WriteOperation operation) => operation switch
    {
        WriteOperation.UpdateOne => "update_one",
        _ => operation.ToString().ToLowerInvariant()
    };

    /// <summary>The filter of an update or a delete, which has to name something.</summary>
    public static Answer<JsonObject> Filter(string? text)
    {
        var parsed = QueryRules.Document(text, "filter");
        if (parsed.Value is not { } filter)
            return parsed;

        return filter.Count == 0 ? Answer.Failed(Messages.EmptyFilter, Messages.EmptyFilterHint, ToolFailures.Arguments) : parsed;
    }

    /// <summary>The update's operators, or why they are not ones a proposal may use.</summary>
    public static Answer<JsonObject> Update(string? text)
    {
        var parsed = QueryRules.Document(text, "update");
        if (parsed.Value is not { } update)
            return parsed;

        string? stray = update.Select(pair => pair.Key).FirstOrDefault(key => !Operators.Contains(key, StringComparer.Ordinal));
        if (update.Count == 0 || stray is not null)
            return Answer.Failed(Messages.BadUpdate(stray), Messages.UpdateHint, ToolFailures.Arguments);

        return parsed;
    }

    /// <summary>The documents an insert adds, or why they are not documents it may add.</summary>
    public static Answer<JsonArray> Documents(string? text, int most)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(text ?? string.Empty, documentOptions: QueryRules.Strict);
        }
        catch (JsonException)
        {
            parsed = null;
        }

        if (parsed is not JsonArray documents || documents.Count == 0 || documents.Any(document => document is not JsonObject))
            return Answer.Failed(Messages.NotInsertable, Messages.ExtendedJsonHint, ToolFailures.Arguments);

        if (documents.Count > most)
            return Answer.Failed(Messages.TooMany(documents.Count, most), Messages.TooManyHint, FailureTypes.TooLarge);

        return Answer.Ok(documents);
    }
}
