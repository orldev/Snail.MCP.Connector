using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Snail.MCP.Connector.Adapters;
using Snail.MCP.Connector.Application;
using Snail.MCP.Connector.Application.Ports;
using Snail.MCP.Connector.Configuration;
using Snail.MCP.Connector.Tests.Application;
using Xunit.Abstractions;

namespace Snail.MCP.Connector.Tests.Chaos;

/// <summary>Exports as a person opens them and as the journal promises them: codes kept as written, nothing that runs, no file unrecorded.</summary>
[Collection(MeasuredCollection.Name)]
public sealed class ExportChaosTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = TestFolder.Create();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>
    /// A cell becomes a number when it parses as one: the postal code of Barcelona, 08001, is 8001 in the workbook, and a
    /// phone written +34600111222 loses its plus. CSV keeps both; the same export as Excel does not.
    /// </summary>
    [Fact]
    public async Task WriteAsync_XlsxWithCodesOfDigits_KeepsThemAsWritten()
    {
        string[] rows = ["""{"zip": "08001", "phone": "+34600111222"}"""];

        await new TableFiles(Microsoft.Extensions.Options.Options.Create(new Snail.MCP.Connector.Configuration.ConnectorOptions())).WriteAsync(_root, "codes.xlsx", TableFormat.Xlsx, Stream(rows), 100, CancellationToken.None);

        using var workbook = SpreadsheetDocument.Open(Path.Combine(_root, "exports", "codes.xlsx"), isEditable: false);
        var cells = workbook.WorkbookPart!.WorksheetParts.Single().Worksheet!.Descendants<Cell>().ToList();
        Assert.Equal("08001", cells[2].InnerText);
        Assert.Equal("+34600111222", cells[3].InnerText);
    }

    /// <summary>
    /// A field a stranger wrote — a customer's name from a web form — that starts with <c>=</c> is a formula once the
    /// person who asked for the export opens it in Excel (CSV injection). Quoting does not stop it.
    /// </summary>
    [Fact]
    public async Task WriteAsync_CsvCellThatStartsWithEquals_IsNotAFormula()
    {
        string[] rows = ["""{"name": "=HYPERLINK(\"http://example.invalid\",\"Invoice\")"}"""];

        await new TableFiles(Microsoft.Extensions.Options.Options.Create(new Snail.MCP.Connector.Configuration.ConnectorOptions())).WriteAsync(_root, "names.csv", TableFormat.Csv, Stream(rows), 100, CancellationToken.None);

        string[] lines = await File.ReadAllLinesAsync(Path.Combine(_root, "exports", "names.csv"), Encoding.UTF8);
        Assert.DoesNotMatch("^\"?[=+@-]", lines[1]);
    }

    /// <summary>
    /// "An answer whose query could not be written down is withheld." The export's file is written into the task's
    /// working copy before the journal line; when the journal cannot hold the line, the answer is withheld and the file
    /// with the company's rows stays where the agent and the platform pick it up.
    /// </summary>
    [Fact]
    public async Task ExportAsync_JournalCannotHoldIt_LeavesNoFileInTheCopy()
    {
        var fakes = new Fakes { Root = $"{_root}/{Fakes.TaskId}/step-0" };
        fakes.Written.IsDown = true;
        var options = Options.Create(new ConnectorOptions { WorkspacesRoot = _root });
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString($"?root={fakes.Root}");
        var copy = new SessionCopy(new HttpContextAccessor { HttpContext = context }, options);
        var queries = new Queries(new Fakes.SourceList(fakes.Sources), fakes.Database, fakes.Tables, fakes.Written, new TableFiles(Microsoft.Extensions.Options.Options.Create(new Snail.MCP.Connector.Configuration.ConnectorOptions())), copy, TimeProvider.System, options);

        var exported = await queries.ExportAsync("sales", "orders", "{}", null, null, "csv", "orders", CancellationToken.None);

        Assert.Equal(Messages.JournalDown, exported.Error);
        Assert.False(File.Exists(Path.Combine(fakes.Root, "exports", "orders.csv")));
    }

    /// <summary>
    /// The most an export may hold, 100 000 rows, of forty fields each: every row is kept as a dictionary until the end and
    /// the workbook is built as one DOM in memory. Measured against 512 MB, the memory a server container is usually given.
    /// </summary>
    [Fact]
    public async Task WriteAsync_MostRowsOfFortyFieldsAsXlsx_StaysUnderHalfAGigabyte()
    {
        long peak = await PeakAsync(() => new TableFiles(Microsoft.Extensions.Options.Options.Create(new Snail.MCP.Connector.Configuration.ConnectorOptions())).WriteAsync(_root, "wide.xlsx", TableFormat.Xlsx, Wide(100_000, 40), 100_000, CancellationToken.None));

        output.WriteLine($"peak managed heap {peak / 1024 / 1024} MB");
        Assert.True(peak < 512L * 1024 * 1024, $"peak managed heap {peak / 1024 / 1024} MB");
    }

    /// <summary>
    /// Documents whose keys are data — stock by date, prices by shop — give a column per key, and nothing bounds the
    /// columns: ten thousand rows of ten new keys each are a table of 100 000 columns, a billion cells, mostly commas.
    /// (The list the columns are looked up in was suspected first; a set in its place took 36.4 s to 34.0 s — not it.)
    /// </summary>
    [Fact]
    public async Task WriteAsync_RowsWhoseKeysAreData_FinishesWithinSeconds()
    {
        var watch = Stopwatch.StartNew();

        await new TableFiles(Microsoft.Extensions.Options.Options.Create(new Snail.MCP.Connector.Configuration.ConnectorOptions())).WriteAsync(_root, "sparse.csv", TableFormat.Csv, Sparse(10_000, 10), 100_000, CancellationToken.None);

        watch.Stop();
        output.WriteLine($"took {watch.Elapsed.TotalSeconds:0.0} s");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed.TotalSeconds:0.0} s");
    }

    private static async Task<long> PeakAsync(Func<Task> work)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        long before = GC.GetTotalMemory(forceFullCollection: true);
        long peak = 0;
        using var stop = new CancellationTokenSource();

        var sampling = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                peak = Math.Max(peak, GC.GetTotalMemory(forceFullCollection: false) - before);
                await Task.Delay(20, CancellationToken.None);
            }
        });

        await work();
        peak = Math.Max(peak, GC.GetTotalMemory(forceFullCollection: false) - before);
        await stop.CancelAsync();
        await sampling;

        return peak;
    }

    private static async IAsyncEnumerable<string> Wide(int rows, int fields, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (int row = 0; row < rows; row++)
        {
            var cells = Enumerable.Range(0, fields).Select(field => $"\"field{field}\": \"value {row}-{field}\"");
            yield return $"{{{string.Join(", ", cells)}}}";
        }

        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<string> Sparse(int rows, int keys, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (int row = 0; row < rows; row++)
        {
            var cells = Enumerable.Range(0, keys).Select(key => $"\"stock_{row}_{key}\": {key}");
            yield return $"{{{string.Join(", ", cells)}}}";
        }

        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<string> Stream(IEnumerable<string> rows, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (string row in rows)
        {
            await Task.Yield();
            yield return row;
        }
    }
}

/// <summary>Tests that measure the process's memory or time, run with nothing else beside them.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MeasuredCollection
{
    /// <summary>The collection's name.</summary>
    public const string Name = "measured";
}
