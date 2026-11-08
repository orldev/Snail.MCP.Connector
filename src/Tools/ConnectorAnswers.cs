using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using Snail.MCP.Connector.Application;
using Snail.MCP.Connector.Application.Ports;

namespace Snail.MCP.Connector.Tools;

/// <summary>What each answer of this server carries as its data, in the envelope of <see cref="ToolAnswer"/>.</summary>
/// <remarks>The envelope — ok first, error and hint on a failure, flagged an error — is the SDK's; the fields inside the data are this server's.</remarks>
public static class ConnectorAnswers
{
    /// <summary>The sources, without their credentials.</summary>
    public static CallToolResult From(Answer<IReadOnlyList<Source>> answer) =>
        ToolAnswer.From(answer, sources => new JsonObject { ["sources"] = new JsonArray([.. sources.Select(Data)]), ["note"] = answer.Hint });

    /// <summary>The collections of a source an agent may read.</summary>
    public static CallToolResult From(Answer<SourceCollections> answer) =>
        ToolAnswer.From(answer, listed => new JsonObject
        {
            ["source"] = listed.Source.Name,
            ["database"] = listed.Source.Database,
            ["description"] = listed.Source.Description,
            ["collections"] = new JsonArray([.. listed.Collections.Select(name => JsonValue.Create(name))])
        });

    /// <summary>A collection's shape.</summary>
    public static CallToolResult From(Answer<CollectionShape> answer) =>
        ToolAnswer.From(answer, shape => new JsonObject
        {
            ["collection"] = shape.Name,
            ["documents"] = shape.Documents,
            ["sampled"] = shape.Sampled,
            ["indexes"] = new JsonArray([.. shape.Indexes.Select(index => JsonValue.Create(index))]),
            ["fields"] = new JsonArray([.. shape.Fields.Select(Data)])
        });

    /// <summary>A page of rows.</summary>
    public static CallToolResult From(Answer<RowPage> answer) =>
        ToolAnswer.From(answer, page => new JsonObject
        {
            ["count"] = page.Rows.Count,
            ["cut"] = page.IsCut,
            ["rows"] = new JsonArray([.. page.Rows.Select(row => JsonNode.Parse(row))]),
            ["note"] = page.IsCut ? Messages.Cut : null
        });

    /// <summary>A count.</summary>
    public static CallToolResult From(Answer<long> answer) => ToolAnswer.From(answer, count => new JsonObject { ["count"] = count });

    /// <summary>A written export.</summary>
    public static CallToolResult From(Answer<ExportedFile> answer) => ToolAnswer.From(answer, Data);

    /// <summary>The task's queries.</summary>
    public static CallToolResult From(Answer<IReadOnlyList<QueryRecord>> answer) =>
        ToolAnswer.From(answer, queries => new JsonObject { ["queries"] = new JsonArray([.. queries.Select(Data)]) });

    /// <summary>A proposal to write, or how one ran.</summary>
    public static CallToolResult From(Answer<WriteProposal> answer) => ToolAnswer.From(answer, Data);

    /// <summary>A proposal as the agent and the dashboard read it.</summary>
    public static JsonObject Data(WriteProposal proposal) => new()
    {
        ["proposal"] = proposal.Id,
        ["state"] = ToolAnswer.Word(proposal.State),
        ["source"] = proposal.Source,
        ["collection"] = proposal.Collection,
        ["operation"] = WriteRules.Word(proposal.Operation),
        ["filter"] = JsonNode.Parse(proposal.Filter),
        ["change"] = JsonNode.Parse(proposal.Change),
        ["reason"] = proposal.Reason,
        ["matched"] = proposal.Matched,
        ["sample"] = new JsonArray([.. proposal.Sample.Select(row => JsonNode.Parse(row))]),
        ["at"] = ToolAnswer.Moment(proposal.At),
        ["decidedBy"] = proposal.DecidedBy,
        ["comment"] = proposal.Comment,
        ["written"] = proposal.Written,
        ["next"] = proposal.State switch
        {
            ProposalState.Pending => Messages.Proposed(proposal.Id),
            _ => null
        }
    };

    /// <summary>A written export: the file in the working copy, its rows and columns, and whether it stopped at the most rows.</summary>
    public static JsonObject Data(ExportedFile file) => new()
    {
        ["file"] = file.Path,
        ["rows"] = file.Rows,
        ["columns"] = new JsonArray([.. file.Columns.Select(column => JsonValue.Create(column))]),
        ["cut"] = file.IsCut
    };

    private static JsonObject Data(Source source) => new()
    {
        ["name"] = source.Name,
        ["kind"] = ToolAnswer.Word(source.Kind),
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
        ["at"] = ToolAnswer.Moment(query.At),
        ["source"] = query.Source,
        ["collection"] = query.Collection,
        ["operation"] = query.Operation,
        ["query"] = query.Query,
        ["rows"] = query.Rows,
        ["milliseconds"] = query.Milliseconds,
        ["error"] = query.Error
    };
}
