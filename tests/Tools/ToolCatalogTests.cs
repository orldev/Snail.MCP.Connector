using System.Reflection;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using Snail.MCP.Connector.Application.Ports;
using Snail.MCP.Connector.Configuration;
using Snail.MCP.Connector.Tools;

namespace Snail.MCP.Connector.Tests.Tools;

/// <summary>The descriptions a model reads, held to the code they describe.</summary>
public sealed partial class ToolCatalogTests
{
    private static readonly ConnectorOptions Defaults = new();

    private static IEnumerable<string> Tools()
    {
        var methods = typeof(ConnectorTools).Assembly.GetTypes()
            .Where(type => type.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(type => type.GetMethods());

        return methods.Select(method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name).OfType<string>();
    }

    [Fact]
    public void Tools_EveryOne_IsNamedForThisServer()
    {
        Assert.Equal(
            ["connector_aggregate", "connector_apply", "connector_count", "connector_diagnose", "connector_export", "connector_find", "connector_journal", "connector_propose_write", "connector_query", "connector_schema", "connector_sources"],
            Tools().Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Kinds_EveryEngine_IsNamed()
    {
        foreach (var kind in Enum.GetValues<SourceKind>())
            Assert.Contains(ToolAnswer.Word(kind), Words(ToolDescriptions.Kinds));
    }

    [Fact]
    public void Formats_EveryFormat_IsNamed()
    {
        foreach (var format in Enum.GetValues<TableFormat>())
            Assert.Contains(ToolAnswer.Word(format), Words(ToolDescriptions.Formats));
    }

    [Fact]
    public void Operations_EveryWrite_IsNamedAsTheRulesReadIt()
    {
        foreach (var operation in Enum.GetValues<WriteOperation>())
            Assert.Contains(Words(ToolDescriptions.Operations), word => Snail.MCP.Connector.Application.WriteRules.Operation(word).Value == operation);
    }

    /// <summary>Every update operator the rules let through is named where the model reads how to write one.</summary>
    [Fact]
    public void Update_EveryAllowedOperator_IsNamedInTheHint()
    {
        foreach (string operation in Snail.MCP.Connector.Application.WriteRules.Operators)
            Assert.Contains(operation, Snail.MCP.Connector.Application.Messages.UpdateHint, StringComparison.Ordinal);
    }

    /// <summary>Every number a description names is the default it describes.</summary>
    [Theory]
    [InlineData(ToolDescriptions.Schema, nameof(ConnectorOptions.Sample))]
    [InlineData(ToolDescriptions.Find, nameof(ConnectorOptions.Rows))]
    [InlineData(ToolDescriptions.Find, nameof(ConnectorOptions.MostRows))]
    [InlineData(ToolDescriptions.Find, nameof(ConnectorOptions.MostBytes))]
    [InlineData(ToolDescriptions.Find, nameof(ConnectorOptions.QuerySeconds))]
    [InlineData(ToolDescriptions.Aggregate, nameof(ConnectorOptions.Rows))]
    [InlineData(ToolDescriptions.Aggregate, nameof(ConnectorOptions.MostRows))]
    [InlineData(ToolDescriptions.Aggregate, nameof(ConnectorOptions.MostBytes))]
    [InlineData(ToolDescriptions.Aggregate, nameof(ConnectorOptions.QuerySeconds))]
    [InlineData(ToolDescriptions.Export, nameof(ConnectorOptions.MostExportRows))]
    [InlineData(ToolDescriptions.Export, nameof(ConnectorOptions.MostExportColumns))]
    [InlineData(ToolDescriptions.Query, nameof(ConnectorOptions.Rows))]
    [InlineData(ToolDescriptions.Query, nameof(ConnectorOptions.MostRows))]
    [InlineData(ToolDescriptions.Query, nameof(ConnectorOptions.MostBytes))]
    [InlineData(ToolDescriptions.Query, nameof(ConnectorOptions.QuerySeconds))]
    [InlineData(ToolDescriptions.Limit, nameof(ConnectorOptions.Rows))]
    [InlineData(ToolDescriptions.Limit, nameof(ConnectorOptions.MostRows))]
    [InlineData(ToolDescriptions.JournalMost, nameof(ConnectorOptions.MostRows))]
    [InlineData(ToolDescriptions.ProposeWrite, nameof(ConnectorOptions.MostWritten))]
    [InlineData(ToolDescriptions.Apply, nameof(ConnectorOptions.ProposalDays))]
    public void Description_NumberItNames_IsTheDefault(string description, string option)
    {
        object? value = typeof(ConnectorOptions).GetProperty(option)!.GetValue(Defaults);

        Assert.Contains($"{value}", Number().Matches(description).Select(match => match.Value));
    }

    /// <summary>Every operator the rules refuse is named where a model reads about pipelines.</summary>
    [Fact]
    public void Aggregate_EveryForbiddenOperator_IsNamed()
    {
        foreach (string operation in Snail.MCP.Connector.Application.QueryRules.Forbidden)
            Assert.Contains(operation, ToolDescriptions.Aggregate, StringComparison.Ordinal);
    }

    private static IReadOnlyList<string> Words(string text) => [.. text.Split([' ', ',', '.'], StringSplitOptions.RemoveEmptyEntries)];

    [GeneratedRegex(@"\d+")]
    private static partial Regex Number();
}
