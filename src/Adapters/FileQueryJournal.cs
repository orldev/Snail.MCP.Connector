using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Snail.MCP.Connector.Application;
using Snail.MCP.Connector.Application.Ports;
using Snail.MCP.Connector.Configuration;
using Snail.Sdk.Mcp.Hosting;

namespace Snail.MCP.Connector.Adapters;

/// <summary>The journal of queries as one file of JSON lines a day in the server's data folder.</summary>
/// <remarks>
/// A file a day so a company's retention is a matter of deleting old files, and the server deletes them itself after
/// <see cref="ConnectorOptions.JournalDays"/>. One writer at a time: lines of two queries written at once must not
/// interleave, and a line half written is a record nobody can read.
/// </remarks>
public sealed class FileQueryJournal(string folder, IOptions<ConnectorOptions> options) : IQueryJournal, IDisposable
{
    private readonly SemaphoreSlim _writing = new(1, 1);

    /// <summary>The journal in the server's own data folder.</summary>
    public static FileQueryJournal InDataFolder(IOptions<ConnectorOptions> options) =>
        new(Path.Combine(ServerFolders.DataDirectory(ConnectorSettings.Name), "journal"), options);

    /// <inheritdoc />
    public async Task<Answer<QueryRecord>> WriteAsync(QueryRecord query, CancellationToken cancellationToken)
    {
        string line = $"{JsonSerializer.Serialize(query, JournalJson.Default.QueryRecord)}{Environment.NewLine}";

        await _writing.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(folder);
            await File.AppendAllTextAsync(FileOf(query.At), line, cancellationToken);
            Forget(query.At);

            return Answer.Ok(query);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Answer.Failed<QueryRecord>(Messages.JournalDown, exception.Message);
        }
        finally
        {
            _writing.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<QueryRecord>> ReadAsync(string task, int most, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(folder))
            return [];

        List<QueryRecord> found = [];
        foreach (string file in Directory.EnumerateFiles(folder, "*.jsonl").Order(StringComparer.Ordinal).Reverse())
        {
            IEnumerable<QueryRecord> records = (await File.ReadAllLinesAsync(file, cancellationToken)).Select(Read).OfType<QueryRecord>();
            found.AddRange(records.Where(record => string.Equals(record.Task, task, StringComparison.Ordinal)).Reverse());

            if (found.Count >= most)
                break;
        }

        return [.. found.Take(most)];
    }

    /// <inheritdoc />
    public void Dispose() => _writing.Dispose();

    private string FileOf(DateTimeOffset at) => Path.Combine(folder, $"{at.UtcDateTime:yyyy-MM-dd}.jsonl");

    private void Forget(DateTimeOffset now)
    {
        string oldest = Path.GetFileName(FileOf(now.AddDays(-options.Value.JournalDays)));

        foreach (string file in Directory.EnumerateFiles(folder, "*.jsonl").Where(file => string.CompareOrdinal(Path.GetFileName(file), oldest) < 0))
            File.Delete(file);
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
}

/// <summary>The journal's records as JSON, generated at build time.</summary>
[JsonSerializable(typeof(QueryRecord))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class JournalJson : JsonSerializerContext;
