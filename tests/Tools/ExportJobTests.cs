using System.Text.Json.Nodes;
using Snail.MCP.Connector.Application;
using Snail.MCP.Connector.Configuration;
using Snail.MCP.Connector.Tests.Application;
using Snail.MCP.Connector.Tools;
using Snail.Sdk.Mcp.Hosting;
using Snail.Sdk.Mcp.Hosting.Jobs;

namespace Snail.MCP.Connector.Tests.Tools;

/// <summary>
/// An export as a job of its task: answered with the file when it ends within the wait, with the job when it does not, and
/// never by a model asked to call again. A call given up stops the waiting, not the export.
/// </summary>
public sealed class ExportJobTests
{
    private readonly Fakes _fakes = new();
    private readonly Jobs _jobs = new(ConnectorSettings.Name);

    [Fact]
    public async Task ExportAsync_EndingWithinTheWait_AnswersWithTheFile()
    {
        var result = await Tools().ExportAsync("sales", "csv", "paid", collection: "orders", filter: "{}");

        Assert.True(result.IsOk(), result.Text());
        Assert.Equal("exports/paid.csv", result.Data()?["file"]?.GetValue<string>());
        var report = result.Report()!;
        Assert.Equal(JobState.Succeeded, report.State);
        Assert.Equal("exports/paid.csv", JsonNode.Parse(report.Result!.Value.GetRawText())?["file"]?.GetValue<string>());
    }

    /// <summary>
    /// A file the work could not write ends the job failed with the work's own words, and the answer is that failure —
    /// ok false and flagged — never a success whose data says ok false.
    /// </summary>
    [Fact]
    public async Task ExportAsync_FileRefusedWithinTheWait_AnswersTheRefusalAndEndsTheJobFailed()
    {
        _fakes.FilesRefuse = "The copy is full.";

        var result = await Tools().ExportAsync("sales", "csv", "paid", collection: "orders", filter: "{}");

        Assert.True(result.IsError);
        Assert.False(result.IsOk());
        Assert.Equal("The copy is full.", result.Error());
        Assert.Equal("Free some room in the copy.", result.Hint());
        Assert.Equal("full", result.Type());
        var report = result.Report()!;
        Assert.Equal(JobState.Failed, report.State);
        Assert.Equal("full", report.Type);
    }

    /// <summary>The job is the task's: a step of another task holding its URI reads nothing.</summary>
    [Fact]
    public async Task ExportAsync_StillWritingWhenTheWaitRunsOut_AnswersWithTheJobOfTheTask()
    {
        _fakes.Held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var result = await Tools().ExportAsync("sales", "xlsx", "paid", collection: "orders", filter: "{}", wait: 0);

        var data = result.Data()!;
        string uri = data["uri"]!.GetValue<string>();
        Assert.StartsWith("job://connector/export/", uri, StringComparison.Ordinal);
        Assert.Equal("running", data["state"]?.GetValue<string>());
        Assert.Equal("exports/paid.xlsx", data["file"]?.GetValue<string>());
        Assert.Contains("exports/paid.xlsx", data["note"]?.GetValue<string>(), StringComparison.Ordinal);
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
        Assert.Contains(Messages.BadFormat, result.Error(), StringComparison.Ordinal);
        Assert.Equal(ToolFailures.Arguments, result.Type());
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
        Assert.Contains("wait", result.Error(), StringComparison.Ordinal);
        Assert.Equal(ToolFailures.Arguments, result.Type());
        Assert.Empty(_fakes.Written.Records);
    }

    private ConnectorTools Tools() => new(_fakes.Queries(), _jobs);
}
