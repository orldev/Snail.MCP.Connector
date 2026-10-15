using System.Globalization;
using System.Text.Json.Nodes;
using Snail.MCP.Connector.Application;

namespace Snail.MCP.Connector.Adapters;

/// <summary>A statement the server wrote and the values it binds as <c>@p0</c>, <c>@p1</c>… in order.</summary>
/// <param name="Text">The statement.</param>
/// <param name="Values">The values, plain JSON: text, a number, true, false or null.</param>
public sealed record SqlStatement(string Text, IReadOnlyList<JsonNode?> Values);

/// <summary>The statements of a proposed write, written by the server from columns and values an agent named.</summary>
/// <remarks>
/// Names come in checked by <see cref="SqlWriteRules"/> and go out quoted in the engine's way; every value is a
/// parameter. Nothing an agent wrote reaches the database as text of a statement.
/// </remarks>
public static class SqlStatements
{
    /// <summary>How many rows the filter matches.</summary>
    public static SqlStatement Count(SourceKind kind, string table, JsonObject filter)
    {
        var values = new List<JsonNode?>();

        return new SqlStatement($"SELECT COUNT(*) FROM {table} WHERE {Where(kind, filter, values)}", values);
    }

    /// <summary>A few of the rows the filter matches.</summary>
    public static SqlStatement Sample(SourceKind kind, string table, JsonObject filter, int sample)
    {
        var values = new List<JsonNode?>();
        string where = Where(kind, filter, values);
        string text = kind is SourceKind.PostgreSql
            ? $"SELECT * FROM {table} WHERE {where} LIMIT {sample.ToString(CultureInfo.InvariantCulture)}"
            : $"SELECT TOP ({sample.ToString(CultureInfo.InvariantCulture)}) * FROM {table} WHERE {where}";

        return new SqlStatement(text, values);
    }

    /// <summary>The statements that make the write: one for an update or a delete, one a row for an insert.</summary>
    public static IReadOnlyList<SqlStatement> Write(SourceKind kind, string table, WriteProposal proposal) => proposal.Operation switch
    {
        WriteOperation.Insert => [.. JsonNode.Parse(proposal.Change)!.AsArray().Select(row => Insert(kind, table, row!.AsObject()))],
        WriteOperation.Delete => [Delete(kind, table, JsonNode.Parse(proposal.Filter)!.AsObject())],
        _ => [Update(kind, table, JsonNode.Parse(proposal.Filter)!.AsObject(), JsonNode.Parse(proposal.Change)!.AsObject())]
    };

    /// <summary>A name quoted in the engine's way.</summary>
    public static string Quoted(SourceKind kind, string name) => kind is SourceKind.PostgreSql
        ? $"\"{name.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
        : $"[{name.Replace("]", "]]", StringComparison.Ordinal)}]";

    private static SqlStatement Update(SourceKind kind, string table, JsonObject filter, JsonObject change)
    {
        var values = new List<JsonNode?>();
        var assignments = new List<string>();

        foreach (var operation in change)
        {
            foreach (var column in operation.Value!.AsObject())
            {
                string name = Quoted(kind, column.Key);
                string value = Bound(values, column.Value);
                assignments.Add(operation.Key == "$inc" ? $"{name} = {name} + {value}" : $"{name} = {value}");
            }
        }

        string where = Where(kind, filter, values);

        return new SqlStatement($"UPDATE {table} SET {string.Join(", ", assignments)} WHERE {where}", values);
    }

    private static SqlStatement Delete(SourceKind kind, string table, JsonObject filter)
    {
        var values = new List<JsonNode?>();

        return new SqlStatement($"DELETE FROM {table} WHERE {Where(kind, filter, values)}", values);
    }

    private static SqlStatement Insert(SourceKind kind, string table, JsonObject row)
    {
        var values = new List<JsonNode?>();
        string columns = string.Join(", ", row.Select(pair => Quoted(kind, pair.Key)));
        string bound = string.Join(", ", row.Select(pair => Bound(values, pair.Value)));

        return new SqlStatement($"INSERT INTO {table} ({columns}) VALUES ({bound})", values);
    }

    private static string Where(SourceKind kind, JsonObject filter, List<JsonNode?> values)
    {
        var conditions = new List<string>();

        foreach (var column in filter)
        {
            string name = Quoted(kind, column.Key);
            if (column.Value is JsonObject comparisons)
            {
                foreach (var comparison in comparisons)
                    conditions.Add(Compared(name, comparison.Key, comparison.Value, values));
            }
            else
            {
                conditions.Add(Compared(name, "$eq", column.Value, values));
            }
        }

        return string.Join(" AND ", conditions);
    }

    private static string Compared(string name, string comparison, JsonNode? operand, List<JsonNode?> values) => comparison switch
    {
        "$eq" when operand is null => $"{name} IS NULL",
        "$ne" when operand is null => $"{name} IS NOT NULL",
        "$null" => operand!.GetValue<bool>() ? $"{name} IS NULL" : $"{name} IS NOT NULL",
        "$in" => $"{name} IN ({string.Join(", ", operand!.AsArray().Select(value => Bound(values, value)))})",
        _ => $"{name} {Operator(comparison)} {Bound(values, operand)}"
    };

    private static string Operator(string comparison) => comparison switch
    {
        "$ne" => "<>",
        "$gt" => ">",
        "$gte" => ">=",
        "$lt" => "<",
        "$lte" => "<=",
        _ => "="
    };

    private static string Bound(List<JsonNode?> values, JsonNode? value)
    {
        values.Add(value?.DeepClone());

        return $"@p{(values.Count - 1).ToString(CultureInfo.InvariantCulture)}";
    }
}
