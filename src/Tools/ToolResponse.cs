using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using Snail.MCP.Connector.Application;
using Snail.MCP.Connector.Application.Ports;
using Snail.Sdk.Mcp.Hosting.Jobs;

namespace Snail.MCP.Connector.Tools;

/// <summary>Answers as tool results: JSON with an ok flag, the data on success, error and hint otherwise.</summary>
public static class ToolResponse
{
    /// <summary>How the answers are written: compact, and every letter as it is rather than escaped.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>The sources, without their credentials.</summary>
    public static CallToolResult From(Answer<IReadOnlyList<Source>> answer) =>
        Of(answer, sources => new JsonObject { ["sources"] = new JsonArray([.. sources.Select(Data)]), ["note"] = answer.Hint });

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
    public static CallToolResult From(Answer<ExportedFile> answer) => Of(answer, Data);

    /// <summary>An export's outcome as its job keeps it: the answer the tool gives, ok with the file or the refusal.</summary>
    public static JsonObject Outcome(Answer<ExportedFile> answer) =>
        answer.Value is { } file
            ? new JsonObject { ["ok"] = true, ["data"] = Data(file) }
            : Refusal(answer.Error!, answer.Hint);

    /// <summary>The tool result of an export's job as it stands, with the job's report beside it.</summary>
    /// <param name="report">The job, ended or still running.</param>
    /// <param name="file">The file it writes, for an answer given while it runs.</param>
    public static CallToolResult Exported(JobReport report, string file) => JobAnswer.Of(report, standing => Said(standing, file));

    private static CallToolResult Said(JobReport report, string file)
    {
        if (report.IsRunning)
        {
            var running = new JsonObject { ["job"] = report.Uri, ["state"] = Word(report.State), ["file"] = file, ["note"] = Messages.ExportRunning(file) };

            return new CallToolResult { Content = [new TextContentBlock { Text = new JsonObject { ["ok"] = true, ["data"] = running }.ToJsonString(Options) }] };
        }

        if (report.State is JobState.Cancelled)
            return Failure(Messages.ExportStopped(report.Message), Messages.ExportAgainHint);

        if (report.State is not JobState.Succeeded || report.Result is not { } result || JsonNode.Parse(result.GetRawText()) is not JsonObject told)
            return Failure(Messages.ExportBroke(report.Message), Messages.ExportAgainHint);

        bool isOk = told["ok"]?.GetValue<bool>() is true;

        return new CallToolResult { IsError = !isOk, Content = [new TextContentBlock { Text = told.ToJsonString(Options) }] };
    }

    private static JsonObject Data(ExportedFile file) => new()
    {
        ["file"] = file.Path,
        ["rows"] = file.Rows,
        ["columns"] = new JsonArray([.. file.Columns.Select(column => JsonValue.Create(column))]),
        ["cut"] = file.IsCut
    };

    /// <summary>The task's queries.</summary>
    public static CallToolResult From(Answer<IReadOnlyList<QueryRecord>> answer) =>
        Of(answer, queries => new JsonObject { ["queries"] = new JsonArray([.. queries.Select(Data)]) });

    /// <summary>A proposal to write, or how one ran.</summary>
    public static CallToolResult From(Answer<WriteProposal> answer) =>
        Of(answer, proposal => Data(proposal));

    /// <summary>A refusal as every answer of this server words it: ok false, what went wrong and what to do.</summary>
    public static JsonObject Refusal(string error, string? hint) => new() { ["ok"] = false, ["error"] = error, ["hint"] = hint };

    /// <summary>A proposal as the agent and the dashboard read it.</summary>
    public static JsonObject Data(WriteProposal proposal) => new()
    {
        ["proposal"] = proposal.Id,
        ["state"] = Word(proposal.State),
        ["source"] = proposal.Source,
        ["collection"] = proposal.Collection,
        ["operation"] = WriteRules.Word(proposal.Operation),
        ["filter"] = JsonNode.Parse(proposal.Filter),
        ["change"] = JsonNode.Parse(proposal.Change),
        ["reason"] = proposal.Reason,
        ["matched"] = proposal.Matched,
        ["sample"] = new JsonArray([.. proposal.Sample.Select(row => JsonNode.Parse(row))]),
        ["at"] = proposal.At.ToString("O", CultureInfo.InvariantCulture),
        ["decidedBy"] = proposal.DecidedBy,
        ["comment"] = proposal.Comment,
        ["written"] = proposal.Written,
        ["next"] = proposal.State switch
        {
            ProposalState.Pending => Messages.Proposed(proposal.Id),
            _ => null
        }
    };

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
        Content = [new TextContentBlock { Text = Refusal(error, hint).ToJsonString(Options) }],
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
