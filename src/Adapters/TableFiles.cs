using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Snail.MCP.Connector.Application;
using Snail.MCP.Connector.Application.Ports;

namespace Snail.MCP.Connector.Adapters;

/// <summary>Rows written as a CSV or an Excel file into the task's working copy, under <c>exports/</c>.</summary>
/// <remarks>
/// Each row is a document; its fields become columns, a nested field a column of its dotted path, an array one column of
/// its JSON. The columns are known only once every row is read, so the rows are held until then — an export stops at
/// <see cref="Configuration.ConnectorOptions.MostExportRows"/>, which bounds what is held. The file lands in the copy
/// the platform hands the step, so it comes back with the task's other files.
/// </remarks>
public sealed class TableFiles : ITableFiles
{
    /// <summary>The folder of the working copy exports are written into.</summary>
    public const string Folder = "exports";

    /// <inheritdoc />
    public async Task<Answer<ExportedFile>> WriteAsync(string copy, string file, TableFormat format, IAsyncEnumerable<string> rows, int most, CancellationToken cancellationToken)
    {
        List<Dictionary<string, string>> table = [];
        List<string> columns = [];
        bool isCut = false;

        try
        {
            await foreach (string row in rows.WithCancellation(cancellationToken))
            {
                if (table.Count == most)
                {
                    isCut = true;
                    break;
                }

                table.Add(Flattened(row, columns));
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && exception is not OperationCanceledException)
        {
            return Answer.Failed<ExportedFile>(Messages.DatabaseSaid("export", MongoDocuments.Scrubbed(exception.Message)), Messages.ExtendedJsonHint);
        }

        string relative = $"{Folder}/{file}";
        string path = Path.Combine(copy, Folder, file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (format is TableFormat.Csv)
            await File.WriteAllTextAsync(path, Csv(columns, table), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), cancellationToken);
        else
            Xlsx(path, columns, table);

        return Answer.Ok(new ExportedFile(relative, table.Count, columns, isCut));
    }

    private static Dictionary<string, string> Flattened(string row, List<string> columns)
    {
        var cells = new Dictionary<string, string>(StringComparer.Ordinal);

        if (JsonNode.Parse(row) is JsonObject document)
            Flatten(document, string.Empty, cells);

        foreach (string column in cells.Keys.Where(column => !columns.Contains(column, StringComparer.Ordinal)))
            columns.Add(column);

        return cells;
    }

    private static void Flatten(JsonObject document, string prefix, Dictionary<string, string> cells)
    {
        foreach (var pair in document)
        {
            string column = prefix.Length == 0 ? pair.Key : $"{prefix}.{pair.Key}";

            if (pair.Value is JsonObject inner && !IsTyped(inner))
                Flatten(inner, column, cells);
            else
                cells[column] = Text(pair.Value);
        }
    }

    /// <summary>Whether the object is one value written the Extended JSON way, such as <c>{"$date": "…"}</c>, rather than a document.</summary>
    private static bool IsTyped(JsonObject inner) => inner.Count == 1 && inner.First().Key.StartsWith('$');

    private static string Text(JsonNode? value) => value switch
    {
        null => string.Empty,
        JsonObject typed when IsTyped(typed) => Text(typed.First().Value),
        JsonValue scalar when scalar.GetValueKind() is JsonValueKind.String => scalar.GetValue<string>(),
        _ => value.ToJsonString()
    };

    private static string Csv(IReadOnlyList<string> columns, IReadOnlyList<Dictionary<string, string>> table)
    {
        var text = new StringBuilder();
        text.AppendLine(string.Join(',', columns.Select(Quoted)));

        foreach (var row in table)
            text.AppendLine(string.Join(',', columns.Select(column => Quoted(row.GetValueOrDefault(column, string.Empty)))));

        return text.ToString();
    }

    private static string Quoted(string cell) =>
        cell.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? $"\"{cell.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : cell;

    private static void Xlsx(string path, IReadOnlyList<string> columns, IReadOnlyList<Dictionary<string, string>> table)
    {
        using var workbook = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var book = workbook.AddWorkbookPart();
        book.Workbook = new Workbook();
        var sheet = book.AddNewPart<WorksheetPart>();
        var data = new SheetData();

        data.AppendChild(Line(columns));
        foreach (var row in table)
            data.AppendChild(Line([.. columns.Select(column => row.GetValueOrDefault(column, string.Empty))]));

        sheet.Worksheet = new Worksheet(data);
        book.Workbook.AppendChild(new Sheets(new Sheet { Id = book.GetIdOfPart(sheet), SheetId = 1, Name = "data" }));
        book.Workbook.Save();
    }

    private static Row Line(IReadOnlyList<string> cells) =>
        new([.. cells.Select(Cell)]);

    private static Cell Cell(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && text.Length < 16
            ? new Cell { DataType = CellValues.Number, CellValue = new CellValue(number) }
            : new Cell { DataType = CellValues.InlineString, InlineString = new InlineString(new Text(text)) };
}
