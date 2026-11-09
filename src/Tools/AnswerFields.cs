using System.Text.Json.Nodes;
using Snail.MCP.Connector.Application;
using Snail.MCP.Connector.Application.Ports;

namespace Snail.MCP.Connector.Tools;

/// <summary>What each answer of this server carries as its data, in the envelope of <see cref="ToolAnswer"/> — one overload of <c>Of</c> per record.</summary>
/// <remarks>The envelope — ok first, error and hint on a failure, flagged an error — is the SDK's; the fields inside the data are this server's.</remarks>
public static class AnswerFields
{
    /// <summary>The sources, without their credentials, and a note naming the ones left out.</summary>
    public static JsonObject Of(DeclaredSources listed) => new()
    {
        ["sources"] = new JsonArray([.. listed.Sources.Select(Of)]),
        ["note"] = Note(listed)
    };

    /// <summary>The sources a listing left out, in words; null when it left out none.</summary>
    public static string? Note(DeclaredSources listed) => listed.Unread.Count == 0 ? null : Messages.SourcesUnread(listed.Unread);

    /// <summary>The collections of a source an agent may read.</summary>
    public static JsonObject Of(SourceCollections listed) => new()
    {
        ["source"] = listed.Source.Name,
        ["database"] = listed.Source.Database,
        ["description"] = listed.Source.Description,
        ["collections"] = new JsonArray([.. listed.Collections.Select(name => JsonValue.Create(name))])
    };

    /// <summary>A collection's shape.</summary>
    public static JsonObject Of(CollectionShape shape) => new()
    {
        ["collection"] = shape.Name,
        ["documents"] = shape.Documents,
        ["sampled"] = shape.Sampled,
        ["indexes"] = new JsonArray([.. shape.Indexes.Select(index => JsonValue.Create(index))]),
        ["fields"] = new JsonArray([.. shape.Fields.Select(Of)])
    };

    /// <summary>A page of rows.</summary>
    public static JsonObject Of(RowPage page) => new()
    {
        ["count"] = page.Rows.Count,
        ["cut"] = page.IsCut,
        ["rows"] = new JsonArray([.. page.Rows.Select(row => JsonNode.Parse(row))]),
        ["note"] = page.IsCut ? Messages.Cut : null
    };

    /// <summary>A count.</summary>
    public static JsonObject Of(long count) => new() { ["count"] = count };

    /// <summary>The task's queries.</summary>
    public static JsonObject Of(IReadOnlyList<QueryRecord> queries) => new() { ["queries"] = new JsonArray([.. queries.Select(Of)]) };

    /// <summary>A proposal as the agent and the dashboard read it.</summary>
    public static JsonObject Of(WriteProposal proposal) => new()
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
    public static JsonObject Of(ExportedFile file) => new()
    {
        ["file"] = file.Path,
        ["rows"] = file.Rows,
        ["columns"] = new JsonArray([.. file.Columns.Select(column => JsonValue.Create(column))]),
        ["cut"] = file.IsCut
    };

    private static JsonObject Of(Source source) => new()
    {
        ["name"] = source.Name,
        ["kind"] = ToolAnswer.Word(source.Kind),
        ["database"] = source.Database,
        ["collections"] = new JsonArray([.. source.Collections.Select(collection => JsonValue.Create(collection))]),
        ["description"] = source.Description
    };

    private static JsonObject Of(FieldShape field) => new()
    {
        ["path"] = field.Path,
        ["types"] = new JsonArray([.. field.Types.Select(type => JsonValue.Create(type))]),
        ["share"] = field.Share,
        ["example"] = field.Example
    };

    private static JsonObject Of(QueryRecord query) => new()
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
