using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using Snail.MCP.Connector.Application;
using Snail.MCP.Connector.Application.Ports;

namespace Snail.MCP.Connector.Tools;

/// <summary>Answers as tool results: JSON with an ok flag, the data on success, error and hint otherwise.</summary>
public static class ToolResponse
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>The sources, without their credentials.</summary>
    public static CallToolResult From(Answer<IReadOnlyList<Source>> answer) =>
        Of(answer, sources => new JsonObject { ["sources"] = new JsonArray([.. sources.Select(Data)]) });

    /// <summary>The collections of a source an agent may read.</summary>
    public static CallToolResult From(Answer<SourceCollections> answer) =>
        Of(answer, listed => new JsonObject
        {
            ["source"] = listed.Source.Name,
            ["database"] = listed.Source.Database,
            ["description"] = listed.Source.Description,
            ["collections"] = new JsonArray([.. listed.Collections.Select(name => JsonValue.Create(name))])
        });

    /// <summary>A collection's shape.</summary>
    public static CallToolResult From(Answer<CollectionShape> answer) =>
        Of(answer, shape => new JsonObject
        {
            ["collection"] = shape.Name,
            ["documents"] = shape.Documents,
            ["sampled"] = shape.Sampled,
            ["indexes"] = new JsonArray([.. shape.Indexes.Select(index => JsonValue.Create(index))]),
            ["fields"] = new JsonArray([.. shape.Fields.Select(Data)])
        });

    /// <summary>A page of rows.</summary>
    public static CallToolResult From(Answer<RowPage> answer) =>
        Of(answer, page => new JsonObject
        {
            ["count"] = page.Rows.Count,
            ["cut"] = page.IsCut,
            ["rows"] = new JsonArray([.. page.Rows.Select(row => JsonNode.Parse(row))]),
            ["note"] = page.IsCut ? Messages.Cut : null
        });

    /// <summary>A count.</summary>
    public static CallToolResult From(Answer<long> answer) => Of(answer, count => new JsonObject { ["count"] = count });

    /// <summary>A written export.</summary>
    public static CallToolResult From(Answer<ExportedFile> answer) =>
        Of(answer, file => new JsonObject
        {
            ["file"] = file.Path,
            ["rows"] = file.Rows,
            ["columns"] = new JsonArray([.. file.Columns.Select(column => JsonValue.Create(column))]),
            ["cut"] = file.IsCut
        });

    /// <summary>The task's queries.</summary>
    public static CallToolResult From(Answer<IReadOnlyList<QueryRecord>> answer) =>
        Of(answer, queries => new JsonObject { ["queries"] = new JsonArray([.. queries.Select(Data)]) });

    /// <summary>A result carrying the data.</summary>
    public static CallToolResult Success(JsonObject data) => new()
    {
        Content = [new TextContentBlock { Text = new JsonObject { ["ok"] = true, ["data"] = data.DeepClone() }.ToJsonString(Options) }],
        StructuredContent = JsonSerializer.SerializeToElement(data, Options)
    };

    /// <summary>A result saying why nothing happened.</summary>
    public static CallToolResult Failure(string error, string? hint) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = new JsonObject { ["ok"] = false, ["error"] = error, ["hint"] = hint }.ToJsonString(Options) }],
        StructuredContent = JsonSerializer.SerializeToElement(new JsonObject { ["error"] = error, ["hint"] = hint }, Options)
    };

    /// <summary>An enum value as the answer spells it.</summary>
    public static string Word<TEnum>(TEnum value) where TEnum : struct, Enum => value.ToString().ToLowerInvariant();

    private static CallToolResult Of<T>(Answer<T> answer, Func<T, JsonObject> data) =>
        answer.Value is { } value && answer.Error is null ? Success(data(value)) : Failure(answer.Error ?? Messages.Nothing, answer.Hint);

    private static JsonObject Data(Source source) => new()
    {
        ["name"] = source.Name,
        ["kind"] = Word(source.Kind),
        ["database"] = source.Database,
        ["collections"] = new JsonArray([.. source.Collections.Select(collection => JsonValue.Create(collection))]),
        ["description"] = source.Description
    };

    private static JsonObject Data(FieldShape field) => new()
    {
        ["path"] = field.Path,
        ["types"] = new JsonArray([.. field.Types.Select(type => JsonValue.Create(type))]),
        ["share"] = field.Share,
        ["example"] = field.Example
    };

    private static JsonObject Data(QueryRecord query) => new()
    {
        ["at"] = query.At.ToString("O", CultureInfo.InvariantCulture),
        ["source"] = query.Source,
        ["collection"] = query.Collection,
        ["operation"] = query.Operation,
        ["query"] = query.Query,
        ["rows"] = query.Rows,
        ["milliseconds"] = query.Milliseconds,
        ["error"] = query.Error
    };
}
