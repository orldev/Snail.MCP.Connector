using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Snail.MCP.Connector.Application.Ports;
using Snail.MCP.Connector.Extensions;
using Snail.MCP.Connector.Tests.Application;

namespace Snail.MCP.Connector.Tests.Tools;

/// <summary>
/// The ways into the server a model takes when it does not know a tool's name or wants several queries in one call:
/// <c>find_tool</c> finds a tool by a task in its own words, and <c>run_tools</c> and <c>run_program</c> send the tools
/// themselves — each command held to the session's grant and written into the task's journal like any call.
/// </summary>
public sealed class DiscoveryTests : IAsyncLifetime
{
    private readonly Fakes _fakes = new();
    private InProcessServer _server = null!;

    public async Task InitializeAsync() =>
        _server = await InProcessServer.StartAsync(
            builder =>
            {
                builder.ConfigureConnectorServer();
                builder.Services.AddSingleton<ISources>(new Fakes.SourceList(_fakes.Sources));
                builder.Services.AddSingleton<IDocuments>(_fakes.Database);
                builder.Services.AddSingleton<IQueryJournal>(_fakes.Written);
                builder.Services.AddSingleton<IProposals>(_fakes.Kept);
            },
            app => app.UseConnectorEndpoints());

    public async Task DisposeAsync() => await _server.DisposeAsync();

    [Theory]
    [InlineData("which databases are there", "connector_sources")]
    [InlineData("what columns does the invoices table have", "connector_schema")]
    [InlineData("how many orders were paid", "connector_count")]
    [InlineData("group the revenue by city", "connector_aggregate")]
    [InlineData("run a select on postgres", "connector_query")]
    [InlineData("save the rows as an excel spreadsheet", "connector_export")]
    [InlineData("what did I already ask before", "connector_journal")]
    [InlineData("change the status of an invoice", "connector_propose_write")]
    public async Task FindTool_ATaskInAModelsOwnWords_FindsTheToolThatDoesIt(string task, string expected)
    {
        var found = await FindAsync(task);

        Assert.True(found["isMatched"]!.GetValue<bool>(), found.ToJsonString());
        Assert.Equal(expected, found["tools"]![0]!["name"]!.GetValue<string>());
    }

    /// <summary>A tool found is named with the command a program sends for it, so the model can use it in run_program at once.</summary>
    [Fact]
    public async Task FindTool_Answer_NamesTheCommandOfAProgram()
    {
        var found = await FindAsync("how many orders were paid");

        Assert.Equal("count", found["tools"]![0]!["command"]!.GetValue<string>());
    }

    /// <summary>
    /// The words a tool is searched by are written beside the tools, so nothing in the compiler notices when a tool is
    /// renamed or added and they are not. This does.
    /// </summary>
    [Fact]
    public async Task Meanings_EveryToolListed_IsKnownByOtherWordsAndNoToolThatIsGone()
    {
        await using var session = await SessionAsync();

        var listed = await session.ListToolsAsync();

        Assert.Equal(listed.Select(tool => tool.Name).Order(StringComparer.Ordinal), ConnectorDiscovery.Meanings.Keys.Order(StringComparer.Ordinal));
    }

    /// <summary>Two queries in one call: both answered, and both in the task's journal as if each had been called.</summary>
    [Fact]
    public async Task RunTools_TwoQueries_AnswersBothAndJournalsEach()
    {
        await using var session = await SessionAsync();
        JsonArray steps =
        [
            new JsonObject { ["tool"] = "connector_count", ["arguments"] = new JsonObject { ["source"] = "sales", ["collection"] = "orders" } },
            new JsonObject { ["tool"] = "connector_find", ["arguments"] = new JsonObject { ["source"] = "sales", ["collection"] = "customers" } }
        ];

        var ran = await session.CallToolAsync("run_tools", new Dictionary<string, object?> { ["steps"] = steps });

        Assert.True(ran.IsOk(), ran.Text());
        Assert.Equal(2, ran.Data()!["ran"]!.GetValue<int>());
        Assert.Equal(42, ran.Data()!["steps"]![0]!["data"]!["count"]!.GetValue<int>());
        Assert.Equal(["count", "find"], _fakes.Written.Records.Select(record => record.Operation));
        Assert.All(_fakes.Written.Records, record => Assert.Equal(Fakes.TaskId, record.Task));
    }

    /// <summary>A program decides as it goes: it counts, and reads only when there is something to read — every call journaled.</summary>
    [Fact]
    public async Task RunProgram_QueriesThatDependOnEachOther_RunInOneCallAndAreJournaled()
    {
        await using var session = await SessionAsync();
        const string code =
            """
            const counted = connector.count({ source: 'sales', collection: 'orders' });
            if (counted.count === 0) return 'nothing';
            const read = connector.find({ source: 'sales', collection: 'orders', limit: 2 });
            return { orders: counted.count, read: read.count };
            """;

        var ran = await session.CallToolAsync("run_program", new Dictionary<string, object?> { ["code"] = code });

        Assert.True(ran.IsOk(), ran.Text());
        Assert.Equal(42, ran.Data()!["result"]!["orders"]!.GetValue<int>());
        Assert.Equal(2, ran.Data()!["result"]!["read"]!.GetValue<int>());
        Assert.Equal(["count", "find"], _fakes.Written.Records.Select(record => record.Operation));
    }

    /// <summary>A session granted the program and one tool cannot reach another through it: the database is never asked.</summary>
    [Fact]
    public async Task RunProgram_ToolTheSessionWasNotGranted_IsRefusedAndNothingIsQueried()
    {
        await using var session = await SessionAsync("tool=run_program&tool=connector_sources");
        const string code = "try { connector.count({ source: 'sales', collection: 'orders' }); } catch (e) { return e.type; }";

        var ran = await session.CallToolAsync("run_program", new Dictionary<string, object?> { ["code"] = code });

        Assert.True(ran.IsOk(), ran.Text());
        Assert.Equal("notGranted", ran.Data()!["result"]!.GetValue<string>());
        Assert.Empty(_fakes.Database.Asked);
        Assert.Empty(_fakes.Written.Records);
    }

    private Task<ModelContextProtocol.Client.McpClient> SessionAsync(string? grant = null) =>
        _server.SessionAsync(grant is null ? $"root=/workspaces/{Fakes.TaskId}/step-0" : $"root=/workspaces/{Fakes.TaskId}/step-0&{grant}");

    private async Task<JsonNode> FindAsync(string task)
    {
        await using var session = await SessionAsync();

        var found = await session.CallToolAsync("find_tool", new Dictionary<string, object?> { ["task"] = task });

        Assert.True(found.IsOk(), found.Text());

        return found.Data()!;
    }
}
