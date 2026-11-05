using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO.Compression;
using System.Xml;
using Microsoft.Extensions.Options;
using Snail.MCP.Connector.Application;
using Snail.MCP.Connector.Application.Ports;
using Snail.MCP.Connector.Configuration;

namespace Snail.MCP.Connector.Adapters;

/// <summary>Rows written as a CSV or an Excel file into the task's working copy, under <c>exports/</c>.</summary>
/// <remarks>
/// Each row is a document; its fields become columns, a nested field a column of its dotted path, an array one column of
/// its JSON. The columns are known only once every row is read, so the rows are read twice: flattened into a spool file
/// of the server's temporary folder, then written from it a row at a time — the CSV line by line, the workbook's sheet
/// as XML straight into its zip entry. Held in memory as dictionaries and one workbook DOM, the most an export may hold,
/// 100 000 rows of forty fields, took 3.5 GB (audit of 30.10.2026); now the columns are all that is held. The workbook is
/// written by hand because the Open XML SDK cannot stream one: a package it opens by path keeps every part in memory
/// until it closes (580 MB for the same export), and one on a stream it may only write it refuses.
/// <para>
/// The columns stop at <see cref="ConnectorOptions.MostExportColumns"/>: documents whose keys are data — stock by date —
/// gave a column per key, 100 000 columns of a billion mostly empty cells. Fields past the most go into one column,
/// <see cref="More"/>, as the JSON of the rest. A number in the workbook is a number only when the data held one: text
/// that reads as a number — the postal code 08001 — stays text. A CSV cell of text that a spreadsheet would run as a
/// formula starts with an apostrophe (OWASP: CSV injection). The file appears whole or not at all: written beside its
/// name and moved into place.
/// </para>
/// </remarks>
public sealed class TableFiles(IOptions<ConnectorOptions> options) : ITableFiles
{
    /// <summary>The folder of the working copy exports are written into.</summary>
    public const string Folder = ITableFiles.Folder;

    /// <summary>The column the fields past the most columns go into, as one JSON object.</summary>
    public const string More = "_more";

    /// <inheritdoc />
    public async Task<Answer<ExportedFile>> WriteAsync(string copy, string file, TableFormat format, IAsyncEnumerable<string> rows, int most, CancellationToken cancellationToken)
    {
        string spool = Path.Combine(Path.GetTempPath(), $"s3l-connector-{Guid.NewGuid():N}.jsonl");
        string path = Path.Combine(copy, Folder, file);
        string written = Path.Combine(copy, Folder, $".{file}.{Guid.NewGuid():N}.partial");

        try
        {
            var spooled = await SpoolAsync(rows, most, spool, cancellationToken);
            if (spooled.Value is not { } table)
                return Answer.Failed<ExportedFile>(spooled.Error!, spooled.Hint);

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (format is TableFormat.Csv)
                await CsvAsync(spool, written, table.Columns, cancellationToken);
            else
                await XlsxAsync(spool, written, table.Columns, cancellationToken);

            File.Move(written, path, overwrite: true);

            return Answer.Ok(new ExportedFile($"{Folder}/{file}", table.Rows, table.Columns, table.IsCut));
        }
        finally
        {
            Remove(spool);
            Remove(written);
        }
    }

    private static void Remove(string file)
    {
        if (File.Exists(file))
            File.Delete(file);
    }

    /// <summary>Reads the rows once, each flattened into a line of the spool; answers the columns, how many rows, and whether the most cut them.</summary>
    private async Task<Answer<SpooledTable>> SpoolAsync(IAsyncEnumerable<string> rows, int most, string spool, CancellationToken cancellationToken)
    {
        var columns = new Columns(options.Value.MostExportColumns);
        long count = 0;
        bool isCut = false;

        await using var stream = File.Create(spool);
        try
        {
            await foreach (string row in rows.WithCancellation(cancellationToken))
            {
                if (count == most)
                {
                    isCut = true;
                    break;
                }

                WriteLine(stream, columns, row);
                count++;
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && exception is not OperationCanceledException)
        {
            return Answer.Failed<SpooledTable>(Messages.DatabaseSaid("export", MongoDocuments.Scrubbed(exception.Message)), Messages.ExtendedJsonHint);
        }

        return Answer.Ok(new SpooledTable(columns.Names, count, isCut));
    }

    /// <summary>One row as a line of the spool: <c>[[column, text, isNumber], …]</c>, the column by its index.</summary>
    private static void WriteLine(Stream stream, Columns columns, string row)
    {
        var cells = new Dictionary<string, TableCell>(StringComparer.Ordinal);
        if (JsonNode.Parse(row) is JsonObject document)
            Flatten(document, string.Empty, cells);

        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartArray();

        JsonObject? more = null;
        foreach (var (column, cell) in cells)
        {
            if (columns.IndexOf(column) is not { } index)
            {
                more ??= [];
                more[column] = cell.IsNumber ? JsonNode.Parse(cell.Text) : cell.Text;
                continue;
            }

            Write(writer, index, cell);
        }

        if (more is not null && columns.IndexOf(More) is { } overflow)
            Write(writer, overflow, new TableCell(more.ToJsonString(), IsNumber: false));

        writer.WriteEndArray();
        writer.Flush();
        stream.WriteByte((byte)'\n');
    }

    private static void Write(Utf8JsonWriter writer, int index, TableCell cell)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(index);
        writer.WriteStringValue(cell.Text);
        writer.WriteBooleanValue(cell.IsNumber);
        writer.WriteEndArray();
    }

    /// <summary>One line of the spool as its cells by column index.</summary>
    private static TableCell[] Cells(string line, int width)
    {
        var row = new TableCell[width];
        Array.Fill(row, TableCell.Empty);

        using var parsed = JsonDocument.Parse(line);
        foreach (var cell in parsed.RootElement.EnumerateArray())
            row[cell[0].GetInt32()] = new TableCell(cell[1].GetString() ?? string.Empty, cell[2].GetBoolean());

        return row;
    }

    private static async Task CsvAsync(string spool, string path, IReadOnlyList<string> columns, CancellationToken cancellationToken)
    {
        await using var text = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        await text.WriteLineAsync(string.Join(',', columns.Select(Quoted)));

        using var reader = new StreamReader(spool, Encoding.UTF8);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
            await text.WriteLineAsync(string.Join(',', Cells(line, columns.Count).Select(cell => Quoted(Inert(cell)))));
    }

    /// <summary>The workbook: one sheet, "data", its rows written into the zip as they are read from the spool.</summary>
    private static async Task XlsxAsync(string spool, string path, IReadOnlyList<string> columns, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        await using var zip = new ZipArchive(stream, ZipArchiveMode.Create);

        await EntryAsync(zip, "[Content_Types].xml", Workbook.ContentTypes, cancellationToken);
        await EntryAsync(zip, "_rels/.rels", Workbook.PackageRelationships, cancellationToken);
        await EntryAsync(zip, "xl/workbook.xml", Workbook.Book, cancellationToken);
        await EntryAsync(zip, "xl/_rels/workbook.xml.rels", Workbook.BookRelationships, cancellationToken);

        await using var sheet = await zip.CreateEntry("xl/worksheets/sheet1.xml").OpenAsync(cancellationToken);
        await using var writer = XmlWriter.Create(sheet, new XmlWriterSettings { Async = true, Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false) });

        await writer.WriteStartDocumentAsync(standalone: true);
        await writer.WriteStartElementAsync(null, "worksheet", Workbook.Main);
        await writer.WriteStartElementAsync(null, "sheetData", Workbook.Main);
        await RowAsync(writer, [.. columns.Select(column => new TableCell(column, IsNumber: false))]);

        using var reader = new StreamReader(spool, Encoding.UTF8);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
            await RowAsync(writer, Cells(line, columns.Count));

        await writer.WriteEndElementAsync();
        await writer.WriteEndElementAsync();
        await writer.WriteEndDocumentAsync();
    }

    private static async Task EntryAsync(ZipArchive zip, string name, string text, CancellationToken cancellationToken)
    {
        await using var entry = await zip.CreateEntry(name).OpenAsync(cancellationToken);
        await entry.WriteAsync(Encoding.UTF8.GetBytes(text), cancellationToken);
    }

    /// <summary>One row of the sheet: a number where the data held one, inline text otherwise.</summary>
    private static async Task RowAsync(XmlWriter writer, IReadOnlyList<TableCell> cells)
    {
        await writer.WriteStartElementAsync(null, "row", Workbook.Main);

        foreach (var cell in cells)
        {
            await writer.WriteStartElementAsync(null, "c", Workbook.Main);
            if (cell.IsNumber && double.TryParse(cell.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && double.IsFinite(number))
            {
                await writer.WriteAttributeStringAsync(null, "t", null, "n");
                await writer.WriteElementStringAsync(null, "v", Workbook.Main, number.ToString("R", CultureInfo.InvariantCulture));
            }
            else
            {
                await writer.WriteAttributeStringAsync(null, "t", null, "inlineStr");
                await writer.WriteStartElementAsync(null, "is", Workbook.Main);
                await writer.WriteStartElementAsync(null, "t", Workbook.Main);
                await writer.WriteAttributeStringAsync("xml", "space", null, "preserve");
                await writer.WriteStringAsync(Legal(cell.Text));
                await writer.WriteEndElementAsync();
                await writer.WriteEndElementAsync();
            }

            await writer.WriteEndElementAsync();
        }

        await writer.WriteEndElementAsync();
    }

    /// <summary>The text without the characters XML cannot hold — a control character a database kept — each a replacement character.</summary>
    private static string Legal(string text)
    {
        if (text.All(XmlConvert.IsXmlChar))
            return text;

        var legal = new StringBuilder(text.Length);
        foreach (char letter in text)
            legal.Append(XmlConvert.IsXmlChar(letter) || char.IsSurrogate(letter) ? letter : '\uFFFD');

        return legal.ToString();
    }

    private static void Flatten(JsonObject document, string prefix, Dictionary<string, TableCell> cells)
    {
        foreach (var pair in document)
        {
            string column = prefix.Length == 0 ? pair.Key : $"{prefix}.{pair.Key}";

            if (pair.Value is JsonObject inner && !IsTyped(inner))
                Flatten(inner, column, cells);
            else
                cells[column] = new TableCell(Text(pair.Value), IsNumber(pair.Value));
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

    /// <summary>Whether the value is a number in the data, rather than text that happens to read as one, as a postal code does.</summary>
    private static bool IsNumber(JsonNode? value) => value switch
    {
        JsonValue scalar => scalar.GetValueKind() is JsonValueKind.Number,
        JsonObject typed when IsTyped(typed) => typed.First().Key is "$numberDecimal" or "$numberLong" or "$numberInt",
        _ => false
    };

    /// <summary>Text a spreadsheet would run as a formula, kept as text by a leading apostrophe; numbers stay as they are.</summary>
    private static string Inert(TableCell cell) =>
        !cell.IsNumber && cell.Text.Length > 0 && cell.Text[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? $"'{cell.Text}" : cell.Text;

    private static string Quoted(string cell) =>
        cell.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? $"\"{cell.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : cell;

    /// <summary>A cell's text and whether the data held it as a number.</summary>
    private readonly record struct TableCell(string Text, bool IsNumber)
    {
        public static TableCell Empty { get; } = new(string.Empty, IsNumber: false);
    }

    /// <summary>The columns of the spooled rows, how many, and whether the most cut the rows.</summary>
    private sealed record SpooledTable(IReadOnlyList<string> Columns, long Rows, bool IsCut);

    /// <summary>The parts of a workbook of one sheet that do not depend on its rows.</summary>
    private static class Workbook
    {
        public const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

        public const string ContentTypes =
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/></Types>""";

        public const string PackageRelationships =
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""";

        public const string Book =
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="data" sheetId="1" r:id="rId1"/></sheets></workbook>""";

        public const string BookRelationships =
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/></Relationships>""";
    }

    /// <summary>The columns met so far, in order, up to the most; the column of the rest is added the first time it is needed.</summary>
    private sealed class Columns(int most)
    {
        private readonly Dictionary<string, int> _indexes = new(StringComparer.Ordinal);

        private readonly List<string> _names = [];

        public IReadOnlyList<string> Names => _names;

        /// <summary>The column's index, the column added when there is room; null when the most is reached.</summary>
        public int? IndexOf(string column)
        {
            if (_indexes.TryGetValue(column, out int known))
                return known;

            if (_names.Count >= most && column != More)
                return null;

            _indexes[column] = _names.Count;
            _names.Add(column);

            return _indexes[column];
        }
    }
}
