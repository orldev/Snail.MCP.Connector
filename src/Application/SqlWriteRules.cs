using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Snail.MCP.Connector.Application;

/// <summary>What a proposed write to a SQL table may be: columns and values, never a statement.</summary>
/// <remarks>
/// The owner's decision of 15.10.2026, carried to SQL: a write runs only after the stand's owner allows it. An agent
/// names the rows by columns and values and the change by <c>$set</c> and <c>$inc</c>, as it does for a document
/// database; the server writes the statement itself, every value a parameter and every name checked and quoted, so no
/// text an agent wrote is ever run against the company's database. The filter has to name something, as with documents.
/// </remarks>
public static partial class SqlWriteRules
{
    /// <summary>The comparisons a filter's column may use; several of a column are joined by AND, as are the columns.</summary>
    public static readonly IReadOnlyList<string> Comparisons = ["$eq", "$ne", "$gt", "$gte", "$lt", "$lte", "$in", "$null"];

    /// <summary>The operators an update may use: <c>$set</c> a value, <c>$inc</c> a number by a number.</summary>
    public static readonly IReadOnlyList<string> Operators = ["$set", "$inc"];

    /// <summary>The filter of an update or a delete: columns, each a value or comparisons; never empty.</summary>
    public static Answer<JsonObject> Filter(string? text)
    {
        if (Parsed(text) is not JsonObject filter)
            return Answer.Failed<JsonObject>(Messages.SqlFilterBad("it is not a JSON object"), Messages.SqlFilterHint);

        if (filter.Count == 0)
            return Answer.Failed<JsonObject>(Messages.EmptyFilter, Messages.SqlFilterHint);

        foreach (var condition in filter)
        {
            if (!IsName(condition.Key))
                return Answer.Failed<JsonObject>(Messages.SqlFilterBad($"\"{condition.Key}\" is not a column name"), Messages.SqlFilterHint);

            if (Wrong(condition.Key, condition.Value) is { } wrong)
                return Answer.Failed<JsonObject>(Messages.SqlFilterBad(wrong), Messages.SqlFilterHint);
        }

        return Answer.Ok(filter);
    }

    /// <summary>The update: <c>$set</c> columns to values, <c>$inc</c> numeric columns by numbers.</summary>
    public static Answer<JsonObject> Update(string? text)
    {
        if (Parsed(text) is not JsonObject update || update.Count == 0)
            return Answer.Failed<JsonObject>(Messages.BadUpdate(null), Messages.SqlUpdateHint);

        foreach (var operation in update)
        {
            if (!Operators.Contains(operation.Key, StringComparer.Ordinal))
                return Answer.Failed<JsonObject>(Messages.BadUpdate(operation.Key), Messages.SqlUpdateHint);

            if (operation.Value is not JsonObject { Count: > 0 } named)
                return Answer.Failed<JsonObject>(Messages.BadUpdate(null), Messages.SqlUpdateHint);

            foreach (var column in named)
            {
                bool isValue = operation.Key == "$inc" ? IsNumber(column.Value) : IsScalar(column.Value);
                if (!IsName(column.Key) || !isValue)
                    return Answer.Failed<JsonObject>(Messages.SqlColumnBad(column.Key), Messages.SqlUpdateHint);
            }
        }

        return Answer.Ok(update);
    }

    /// <summary>The rows an insert adds: objects of columns and plain values.</summary>
    public static Answer<JsonArray> Rows(string? text, int most)
    {
        var listed = WriteRules.Documents(text, most);
        if (listed.Value is not { } rows)
            return listed;

        var columns = rows.Cast<JsonObject>().SelectMany(row => row);
        var wrong = columns.FirstOrDefault(pair => !IsName(pair.Key) || !IsScalar(pair.Value));

        return wrong.Key is null ? listed : Answer.Failed<JsonArray>(Messages.SqlColumnBad(wrong.Key), Messages.SqlInsertHint);
    }

    /// <summary>Whether a table is named as one name or a schema and a name, each a plain identifier.</summary>
    public static bool IsTable(string table) => Table().IsMatch(table);

    /// <summary>Whether a column is a plain identifier, which is quoted when the statement is written.</summary>
    public static bool IsName(string column) => Name().IsMatch(column);

    private static string? Wrong(string column, JsonNode? condition)
    {
        if (condition is not JsonObject comparisons)
            return IsScalar(condition) ? null : $"the value of \"{column}\" is not a plain value";

        if (comparisons.Count == 0)
            return $"\"{column}\" has no comparison";

        foreach (var pair in comparisons)
        {
            string comparison = pair.Key;
            var operand = pair.Value;
            string? wrong = comparison switch
            {
                "$in" => operand is JsonArray { Count: > 0 } values && values.All(IsScalar) ? null : $"$in of \"{column}\" takes a list of values",
                "$null" => operand is JsonValue flag && flag.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? null : $"$null of \"{column}\" takes true or false",
                _ when Comparisons.Contains(comparison, StringComparer.Ordinal) => IsScalar(operand) ? null : $"{comparison} of \"{column}\" takes a plain value",
                _ => $"{comparison} is not a comparison a filter may use"
            };

            if (wrong is not null)
                return wrong;
        }

        return null;
    }

    private static bool IsScalar(JsonNode? node) =>
        node is null || node is JsonValue value && value.GetValueKind() is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False;

    private static bool IsNumber(JsonNode? node) => node is JsonValue value && value.GetValueKind() is JsonValueKind.Number;

    private static JsonNode? Parsed(string? text)
    {
        try
        {
            return JsonNode.Parse(text ?? string.Empty, documentOptions: QueryRules.Strict);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex Name();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,127}(\.[A-Za-z_][A-Za-z0-9_]{0,127})?$", RegexOptions.CultureInvariant)]
    private static partial Regex Table();
}
