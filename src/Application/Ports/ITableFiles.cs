namespace Snail.MCP.Connector.Application.Ports;

/// <summary>The formats rows are exported in.</summary>
public enum TableFormat
{
    /// <summary>Comma-separated values, UTF-8 with a byte order mark so Excel reads the letters right.</summary>
    Csv,

    /// <summary>An Excel workbook of one sheet.</summary>
    Xlsx
}

/// <summary>A file of rows written into a task's working copy.</summary>
/// <param name="Path">The file, relative to the working copy.</param>
/// <param name="Rows">How many rows it holds.</param>
/// <param name="Columns">The columns, in order.</param>
/// <param name="IsCut">Whether the export stopped at its most rows before the data ended.</param>
public sealed record ExportedFile(string Path, long Rows, IReadOnlyList<string> Columns, bool IsCut);

/// <summary>Writes rows as a table file.</summary>
public interface ITableFiles
{
    /// <summary>The folder of the working copy exports are written into.</summary>
    const string Folder = "exports";

    /// <summary>Writes the rows, each a JSON object, as columns of their fields into <paramref name="file"/> under <paramref name="copy"/>.</summary>
    Task<Answer<ExportedFile>> WriteAsync(string copy, string file, TableFormat format, IAsyncEnumerable<string> rows, int most, CancellationToken cancellationToken);
}

/// <summary>An export checked and begun in the journal, whose rows are still to be written.</summary>
/// <param name="Task">The task it is made for, which owns the job that writes it.</param>
/// <param name="File">The file it writes, relative to the working copy.</param>
/// <param name="WriteAsync">Writes the rows and journals how it went, on a token of its own: a job's, which outlives the call.</param>
public sealed record PreparedExport(string Task, string File, Func<CancellationToken, Task<Answer<ExportedFile>>> WriteAsync);
