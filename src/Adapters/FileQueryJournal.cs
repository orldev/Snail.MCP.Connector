using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Snail.MCP.Connector.Application;
using Snail.MCP.Connector.Application.Ports;
using Snail.MCP.Connector.Configuration;
using Snail.Sdk.Mcp.Hosting;

namespace Snail.MCP.Connector.Adapters;

/// <summary>The journal of queries as one file of JSON lines a day in the server's data folder, and a file of each task's lines beside it.</summary>
/// <remarks>
/// A file a day so a company's retention is a matter of deleting old files, and the server deletes them itself after
/// <see cref="ConnectorOptions.JournalDays"/>. One writer at a time: lines of two queries written at once must not
/// interleave, and a line half written is a record nobody can read.
/// <para>
/// A task reads its own lines from <c>tasks/&lt;task&gt;.jsonl</c>, the same line written there too. Read from the day
/// files, a task with fewer lines than asked — every new task — made each connector_journal call read the whole journal
/// of every task on the stand: 4 to 8.7 s for thirty busy days (audit of 30.10.2026). A task's file goes when no line was
/// added to it for the days kept.
/// </para>
/// </remarks>
public sealed partial class FileQueryJournal(string folder, IOptions<ConnectorOptions> options) : IQueryJournal, IDisposable
{
    private readonly SemaphoreSlim _writing = new(1, 1);

    private DateOnly _forgotten;

    /// <summary>The journal in the server's own data folder.</summary>
    public static FileQueryJournal InDataFolder(IOptions<ConnectorOptions> options) =>
        new(Path.Combine(ServerFolders.DataDirectory(ConnectorSettings.Name), "journal"), options);

    private string Tasks => Path.Combine(folder, "tasks");

    /// <inheritdoc />
    public async Task<Answer<QueryRecord>> WriteAsync(QueryRecord query, CancellationToken cancellationToken)
    {
        string line = $"{JsonSerializer.Serialize(query, JournalJson.Default.QueryRecord)}{Environment.NewLine}";

        await _writing.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Tasks);
            await File.AppendAllTextAsync(FileOf(query.At), line, cancellationToken);
            if (TaskFileOf(query.Task) is { } taskFile)
                await File.AppendAllTextAsync(taskFile, line, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Answer.Failed(Messages.JournalDown, exception.Message, FailureTypes.JournalDown);
        }
        finally
        {
            _writing.Release();
        }

        Forget(query.At);

        return Answer.Ok(query);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<QueryRecord>> ReadAsync(string task, int most, CancellationToken cancellationToken)
    {
        if (TaskFileOf(task) is not { } file || !File.Exists(file))
            return [];

        string[] lines = await File.ReadAllLinesAsync(file, cancellationToken);
        IEnumerable<QueryRecord> records = lines.Select(Read).OfType<QueryRecord>();

        return [.. records.Reverse().Take(most)];
    }

    /// <inheritdoc />
    public void Dispose() => _writing.Dispose();

    private string FileOf(DateTimeOffset at) => Path.Combine(folder, $"{at.UtcDateTime:yyyy-MM-dd}.jsonl");

    /// <summary>The task's own file; null for a task whose name is not one a file may carry.</summary>
    private string? TaskFileOf(string task) => TaskName().IsMatch(task) ? Path.Combine(Tasks, $"{task}.jsonl") : null;

    /// <summary>Deletes the days and the tasks older than the days kept, once a day.</summary>
    /// <remarks>
    /// After the line is written, and apart from it: a file that cannot be deleted now is deleted on a later day, and the
    /// query it followed is in the journal all the same — it was answered as down when this ran inside the write.
    /// </remarks>
    private void Forget(DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        if (today == _forgotten)
            return;

        _forgotten = today;
        string oldest = Path.GetFileName(FileOf(now.AddDays(-options.Value.JournalDays)));
        var stale = now.UtcDateTime.AddDays(-options.Value.JournalDays);

        try
        {
            foreach (string file in Directory.EnumerateFiles(folder, "*.jsonl").Where(file => string.CompareOrdinal(Path.GetFileName(file), oldest) < 0))
                File.Delete(file);

            foreach (string file in Directory.EnumerateFiles(Tasks, "*.jsonl").Where(file => File.GetLastWriteTimeUtc(file) < stale))
                File.Delete(file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _forgotten = default;
        }
    }

    private static QueryRecord? Read(string line)
    {
        try
        {
            return JsonSerializer.Deserialize(line, JournalJson.Default.QueryRecord);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,62}$")]
    private static partial Regex TaskName();
}

/// <summary>The journal's records as JSON, generated at build time.</summary>
[JsonSerializable(typeof(QueryRecord))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class JournalJson : JsonSerializerContext;
