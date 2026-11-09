using System.Runtime.CompilerServices;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Snail.MCP.Connector.Adapters;
using Snail.MCP.Connector.Application.Ports;

namespace Snail.MCP.Connector.Tests.Adapters;

/// <summary>Rows written as a table a person opens: nested fields as dotted columns, values quoted where a comma would split them.</summary>
public sealed class TableFilesTests : IDisposable
{
    private readonly string _copy = TestFolder.Create();

    public void Dispose() => Directory.Delete(_copy, recursive: true);

    [Fact]
    public async Task WriteAsync_Csv_FlattensNestedFieldsAndQuotesCommas()
    {
        string[] rows = ["""{"number": "A-1", "customer": {"name": "Улитка, SL", "city": "Madrid"}, "total": 120.5}""", """{"number": "A-2", "date": {"$date": "2026-10-01T00:00:00Z"}}"""];

        var written = await new TableFiles(Microsoft.Extensions.Options.Options.Create(new Snail.MCP.Connector.Configuration.ConnectorOptions())).WriteAsync(_copy, "orders.csv", TableFormat.Csv, Stream(rows), 100, CancellationToken.None);

        Assert.Equal("exports/orders.csv", written.Value!.Path);
        Assert.Equal(["number", "customer.name", "customer.city", "total", "date"], written.Value.Columns);
        string[] lines = await File.ReadAllLinesAsync(Path.Combine(_copy, "exports", "orders.csv"), Encoding.UTF8);
        Assert.Equal("A-1,\"Улитка, SL\",Madrid,120.5,", lines[1]);
        Assert.Equal("A-2,,,,2026-10-01T00:00:00Z", lines[2]);
    }

    [Fact]
    public async Task WriteAsync_MoreRowsThanTheMost_StopsThereAndSaysSo()
    {
        string[] rows = ["""{"n": 1}""", """{"n": 2}""", """{"n": 3}"""];

        var written = await new TableFiles(Microsoft.Extensions.Options.Options.Create(new Snail.MCP.Connector.Configuration.ConnectorOptions())).WriteAsync(_copy, "n.csv", TableFormat.Csv, Stream(rows), 2, CancellationToken.None);

        Assert.Equal(2, written.Value!.Rows);
        Assert.True(written.Value.IsCut);
    }

    [Fact]
    public async Task WriteAsync_Xlsx_IsAWorkbookWithTheHeaderAndNumbers()
    {
        string[] rows = ["""{"number": "A-1", "total": 120.5}"""];

        await new TableFiles(Microsoft.Extensions.Options.Options.Create(new Snail.MCP.Connector.Configuration.ConnectorOptions())).WriteAsync(_copy, "orders.xlsx", TableFormat.Xlsx, Stream(rows), 100, CancellationToken.None);

        using var workbook = SpreadsheetDocument.Open(Path.Combine(_copy, "exports", "orders.xlsx"), isEditable: false);
        var cells = workbook.WorkbookPart!.WorksheetParts.Single().Worksheet!.Descendants<Cell>().ToList();
        Assert.Equal("number", cells[0].InnerText);
        Assert.Equal(CellValues.Number, cells[3].DataType?.Value);
        Assert.Equal("120.5", cells[3].CellValue!.Text);
        Assert.Empty(new DocumentFormat.OpenXml.Validation.OpenXmlValidator().Validate(workbook));
    }

    /// <summary>A control character a database kept is no XML character: the cell holds a replacement character, and the workbook still opens.</summary>
    [Fact]
    public async Task WriteAsync_XlsxWithAControlCharacter_StaysAWorkbook()
    {
        string[] rows = ["""{"note": "bell\u0007end"}"""];

        await new TableFiles(Microsoft.Extensions.Options.Options.Create(new Snail.MCP.Connector.Configuration.ConnectorOptions())).WriteAsync(_copy, "notes.xlsx", TableFormat.Xlsx, Stream(rows), 100, CancellationToken.None);

        using var workbook = SpreadsheetDocument.Open(Path.Combine(_copy, "exports", "notes.xlsx"), isEditable: false);
        var cells = workbook.WorkbookPart!.WorksheetParts.Single().Worksheet!.Descendants<Cell>().ToList();
        Assert.Equal("bell\uFFFDend", cells[1].InnerText);
    }

    /// <summary>Past the most columns, the rest of a row's fields go into one column as JSON, and no file is wider than the most.</summary>
    [Fact]
    public async Task WriteAsync_MoreFieldsThanTheMostColumns_KeepsTheRestAsJson()
    {
        string[] rows = ["""{"a": 1, "b": "x", "c": 2, "d": "y"}"""];
        var files = new TableFiles(Microsoft.Extensions.Options.Options.Create(new Snail.MCP.Connector.Configuration.ConnectorOptions { MostExportColumns = 2 }));

        var written = await files.WriteAsync(_copy, "wide.csv", TableFormat.Csv, Stream(rows), 100, CancellationToken.None);

        Assert.Equal(["a", "b", TableFiles.More], written.Value!.Columns);
        string[] lines = await File.ReadAllLinesAsync(Path.Combine(_copy, "exports", "wide.csv"), Encoding.UTF8);
        Assert.Equal("1,x,\"{\"\"c\"\":2,\"\"d\"\":\"\"y\"\"}\"", lines[1]);
    }

    /// <summary>A database that fails halfway is an answer in words, and its credentials stay out of them.</summary>
    [Fact]
    public async Task WriteAsync_RowsThatFail_AnswerWithoutTheCredentials()
    {
        var written = await new TableFiles(Microsoft.Extensions.Options.Options.Create(new Snail.MCP.Connector.Configuration.ConnectorOptions())).WriteAsync(_copy, "n.csv", TableFormat.Csv, Failing(), 100, CancellationToken.None);

        Assert.Null(written.Value);
        Assert.DoesNotContain("secret", written.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// The task's agent writes its own copy, and makes <c>exports</c> a link to another task's copy: the export of its
    /// rows lands there, in a copy this session was never given.
    /// </summary>
    [Fact]
    public async Task WriteAsync_ExportsALinkOutOfTheCopy_IsRefusedAndWritesNothingThere()
    {
        string elsewhere = TestFolder.Create();
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_copy, "exports"), elsewhere);

            var written = await Files().WriteAsync(_copy, "orders.csv", TableFormat.Csv, Stream(["""{"n": 1}"""]), 100, CancellationToken.None);

            Assert.False(written.IsSuccess);
            Assert.Equal("outside", written.Type);
            Assert.Empty(Directory.EnumerateFileSystemEntries(elsewhere));
        }
        finally
        {
            Directory.Delete(elsewhere, recursive: true);
        }
    }

    /// <summary>A link that stays inside the copy is the copy's own business: the export follows it.</summary>
    [Fact]
    public async Task WriteAsync_ExportsALinkInsideTheCopy_WritesThere()
    {
        string inside = Directory.CreateDirectory(Path.Combine(_copy, "out")).FullName;
        Directory.CreateSymbolicLink(Path.Combine(_copy, "exports"), inside);

        var written = await Files().WriteAsync(_copy, "orders.csv", TableFormat.Csv, Stream(["""{"n": 1}"""]), 100, CancellationToken.None);

        Assert.True(written.IsSuccess, written.Error);
        Assert.True(File.Exists(Path.Combine(inside, "orders.csv")));
    }

    private static TableFiles Files() => new(Microsoft.Extensions.Options.Options.Create(new Snail.MCP.Connector.Configuration.ConnectorOptions()));

    private static async IAsyncEnumerable<string> Stream(IEnumerable<string> rows, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (string row in rows)
        {
            await Task.Yield();
            yield return row;
        }
    }

    private static async IAsyncEnumerable<string> Failing()
    {
        await Task.Yield();
        yield return """{"n": 1}""";
        throw new InvalidOperationException("connection to mongodb://reader:secret@db:27017 was lost");
    }
}
