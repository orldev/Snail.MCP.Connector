using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using Snail.MCP.Connector.Application;
using Snail.MCP.Connector.Tests.Application;
using Snail.MCP.Connector.Tools;
using Snail.Sdk.Mcp.Hosting.Jobs;

namespace Snail.MCP.Connector.Tests.Tools;

/// <summary>
/// An export as a job of its task: answered with the file when it ends within the wait, with the job when it does not, and
/// never by a model asked to call again. A call given up stops the waiting, not the export.
/// </summary>
public sealed class ExportJobTests
{
    private readonly Fakes _fakes = new();
    private readonly Jobs _jobs = new(Snail.MCP.Connector.Extensions.ConnectorServerExtensions.Short);

    [Fact]
    public async Task ExportAsync_EndingWithinTheWait_AnswersWithTheFile()
    {
        var result = await Tools().ExportAsync("sales", "csv", "paid", collection: "orders", filter: "{}");

        Assert.NotEqual(true, result.IsError);
        Assert.Equal("exports/paid.csv", Said(result)["data"]?["file"]?.GetValue<string>());
        var report = Report(result);
        Assert.Equal(JobState.Succeeded, report.State);
        Assert.Equal("exports/paid.csv", JsonNode.Parse(report.Result!.Value.GetRawText())?["data"]?["file"]?.GetValue<string>());
    }

    /// <summary>The job is the task's: a step of another task holding its URI reads nothing.</summary>
    [Fact]
    public async Task ExportAsync_StillWritingWhenTheWaitRunsOut_AnswersWithTheJobOfTheTask()
    {
        _fakes.Held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var result = await Tools().ExportAsync("sales", "xlsx", "paid", collection: "orders", filter: "{}", wait: 0);

        var data = Said(result)["data"]!;
        string uri = data["job"]!.GetValue<string>();
        Assert.StartsWith("job://connector/export/", uri, StringComparison.Ordinal);
        Assert.Equal("running", data["state"]?.GetValue<string>());
        Assert.Equal("exports/paid.xlsx", data["file"]?.GetValue<string>());
        Assert.Null(_jobs.Find(uri, "another-task"));

        _fakes.Held.SetResult();
        var ended = await _jobs.Find(uri, Fakes.TaskId)!.Completion;

        Assert.Equal(JobState.Succeeded, ended.State);
        Assert.Equal(["exporting", "export"], _fakes.Written.Records.Select(record => record.Operation));
    }

    [Fact]
    public async Task ExportAsync_CallGivenUp_StillWritesTheFile()
    {
        _fakes.Held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var givenUp = new CancellationTokenSource();
        var calling = Tools().ExportAsync("sales", "csv", "paid", collection: "orders", filter: "{}", cancellationToken: givenUp.Token);

        await givenUp.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => calling);
        var job = Assert.Single(_jobs.InFlight);
        _fakes.Held.SetResult();
        var ended = await job.Completion;

        Assert.Equal(JobState.Succeeded, ended.State);
        Assert.Equal("export", _fakes.Written.Records[^1].Operation);
    }

    /// <summary>An export the rules refuse is refused while the call is served: no job, nothing in the journal.</summary>
    [Fact]
    public async Task ExportAsync_Refused_StartsNoJob()
    {
        var result = await Tools().ExportAsync("sales", "pdf", "paid", collection: "orders", filter: "{}");

        Assert.True(result.IsError);
        Assert.Contains(Messages.BadFormat, Text(result), StringComparison.Ordinal);
        Assert.Empty(_jobs.InFlight);
        Assert.Empty(_jobs.Recent);
        Assert.Empty(_fakes.Written.Records);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(ToolDescriptions.LongestWait + 1)]
    public async Task ExportAsync_WaitOutsideTheRange_IsRefusedByName(int wait)
    {
        var result = await Tools().ExportAsync("sales", "csv", "paid", collection: "orders", filter: "{}", wait: wait);

        Assert.True(result.IsError);
        Assert.Contains("wait", Text(result), StringComparison.Ordinal);
        Assert.Empty(_fakes.Written.Records);
    }

    private ConnectorTools Tools() => new(_fakes.Queries(), _jobs);

    private static string Text(CallToolResult result) => Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

    private static JsonNode Said(CallToolResult result) => JsonNode.Parse(Text(result))!;

    private static JobReport Report(CallToolResult result) => JobReport.FromJson(result.StructuredContent!.Value.GetRawText())!;
}
