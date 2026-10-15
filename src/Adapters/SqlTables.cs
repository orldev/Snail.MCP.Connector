using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Npgsql;
using Snail.MCP.Connector.Application;
using Snail.MCP.Connector.Application.Ports;

namespace Snail.MCP.Connector.Adapters;

/// <summary>PostgreSQL and SQL Server read by the source's read-only user, inside a transaction that is always rolled back.</summary>
/// <remarks>
/// Three walls: the user cannot write, PostgreSQL's transaction is READ ONLY so the database refuses a write itself, and
/// both are rolled back whatever ran in them. The database stops a statement at the query's seconds (its command
/// timeout). Rows are read one more than asked, so an answer knows it was cut, and reading stops there rather than
/// pulling the rest of a large result over the network.
/// </remarks>
public sealed partial class SqlTables : ITables
{
    private const int Sample = 3;

    private const int ExampleLength = 60;

    /// <inheritdoc />
    public async Task<Answer<IReadOnlyList<string>>> TablesAsync(ReachableSource source, CancellationToken cancellationToken) =>
        await GuardedAsync(source, 30, async (connection, transaction) =>
        {
            const string sql = "SELECT table_schema, table_name FROM information_schema.tables WHERE table_schema NOT IN ('pg_catalog', 'information_schema', 'sys') ORDER BY table_schema, table_name";
            List<string> tables = [];

            await using var command = Command(connection, transaction, sql, 30);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                tables.Add($"{reader.GetString(0)}.{reader.GetString(1)}");

            return (IReadOnlyList<string>)tables;
        }, cancellationToken);

    /// <inheritdoc />
    public async Task<Answer<CollectionShape>> ShapeAsync(ReachableSource source, string table, CancellationToken cancellationToken) =>
        await GuardedAsync(source, 30, async (connection, transaction) =>
        {
            var name = await ResolvedAsync(connection, transaction, table, cancellationToken);
            var columns = await ColumnsAsync(connection, transaction, name, cancellationToken);
            var examples = await ExamplesAsync(source.Source.Kind, connection, transaction, name, cancellationToken);
            long rows = await EstimateAsync(source.Source.Kind, connection, transaction, name, cancellationToken);
            var indexes = await IndexesAsync(source.Source.Kind, connection, transaction, name, cancellationToken);

            IReadOnlyList<FieldShape> fields = [.. columns.Select(column => new FieldShape(column.Name, column.Types, column.IsNullable ? 0.5 : 1, examples.GetValueOrDefault(column.Name)))];

            return new CollectionShape($"{name.Schema}.{name.Table}", rows, examples.Count == 0 ? 0 : Sample, fields) { Indexes = indexes };
        }, cancellationToken);

    /// <inheritdoc />
    public async Task<Answer<RowPage>> QueryAsync(ReachableSource source, string select, RowLimit limit, CancellationToken cancellationToken) =>
        await GuardedAsync(source, limit.Seconds, async (connection, transaction) =>
        {
            List<string> rows = [];
            int bytes = 0;

            await using var command = Command(connection, transaction, select, limit.Seconds);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                string row = Row(reader);
                bytes += Encoding.UTF8.GetByteCount(row);

                if (rows.Count == limit.Rows || bytes > limit.Bytes)
                    return new RowPage(rows, IsCut: true);

                rows.Add(row);
            }

            return new RowPage(rows, IsCut: false);
        }, cancellationToken);

    /// <inheritdoc />
    public async IAsyncEnumerable<string> StreamAsync(ReachableSource source, string select, int most, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var connection = Connection(source);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await ReadOnlyAsync(source.Source.Kind, connection, cancellationToken);

        await using (var command = Command(connection, transaction, select, 600))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            int read = 0;
            while (read <= most && await reader.ReadAsync(cancellationToken))
            {
                read++;
                yield return Row(reader);
            }
        }

        await transaction.RollbackAsync(cancellationToken);
    }

    /// <summary>The message of a failure without a password a connection string may carry.</summary>
    public static string Scrubbed(string message) => Password().Replace(MongoDocuments.Scrubbed(message), "Password=***");

    /// <summary>A row as one compact JSON object, its values as a person reads them.</summary>
    public static string Row(DbDataReader reader)
    {
        var row = new JsonObject();

        for (int index = 0; index < reader.FieldCount; index++)
            row[reader.GetName(index)] = Value(reader.IsDBNull(index) ? null : reader.GetValue(index));

        return row.ToJsonString();
    }

    private static JsonNode? Value(object? value) => value switch
    {
        null => null,
        bool flag => JsonValue.Create(flag),
        byte or short or int or long => JsonValue.Create(Convert.ToInt64(value, CultureInfo.InvariantCulture)),
        decimal number => JsonValue.Create(number),
        float or double => JsonValue.Create(Convert.ToDouble(value, CultureInfo.InvariantCulture)),
        DateTime at => JsonValue.Create(at.ToString("O", CultureInfo.InvariantCulture)),
        DateTimeOffset at => JsonValue.Create(at.ToString("O", CultureInfo.InvariantCulture)),
        DateOnly day => JsonValue.Create(day.ToString("O", CultureInfo.InvariantCulture)),
        byte[] bytes => JsonValue.Create($"<{bytes.Length} bytes>"),
        _ => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture))
    };

    private static DbConnection Connection(ReachableSource source) => source.Source.Kind switch
    {
        SourceKind.PostgreSql => new NpgsqlConnection(new NpgsqlConnectionStringBuilder(source.Access.ReadUri) { Database = source.Source.Database, ApplicationName = "snail-mcp-connector" }.ConnectionString),
        _ => new SqlConnection(new SqlConnectionStringBuilder(source.Access.ReadUri) { InitialCatalog = source.Source.Database, ApplicationName = "snail-mcp-connector" }.ConnectionString)
    };

    /// <summary>A transaction the statement runs in and that is never committed; PostgreSQL's also refuses any write itself.</summary>
    private static async Task<DbTransaction> ReadOnlyAsync(SourceKind kind, DbConnection connection, CancellationToken cancellationToken)
    {
        var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        if (kind is not SourceKind.PostgreSql)
            return transaction;

        await using var command = Command(connection, transaction, "SET TRANSACTION READ ONLY", 30);
        await command.ExecuteNonQueryAsync(cancellationToken);

        return transaction;
    }

    private static DbCommand Command(DbConnection connection, DbTransaction transaction, string sql, int seconds)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.CommandTimeout = seconds;

        return command;
    }

    private static void Parameter(DbCommand command, string name, string value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    /// <summary>The table's schema and name as the database spells them; a bare name is looked up, the usual schema first.</summary>
    private static async Task<TableName> ResolvedAsync(DbConnection connection, DbTransaction transaction, string table, CancellationToken cancellationToken)
    {
        string[] parts = table.Split('.', 2);
        const string sql = "SELECT table_schema, table_name FROM information_schema.tables WHERE lower(table_name) = lower(@table) AND (@schema = '' OR lower(table_schema) = lower(@schema)) ORDER BY CASE WHEN table_schema IN ('public', 'dbo') THEN 0 ELSE 1 END, table_schema";

        await using var command = Command(connection, transaction, sql, 30);
        Parameter(command, "@table", parts[^1]);
        Parameter(command, "@schema", parts.Length == 2 ? parts[0] : string.Empty);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException($"no table is called {table}");

        return new TableName(reader.GetString(0), reader.GetString(1));
    }

    private static async Task<IReadOnlyList<ColumnShape>> ColumnsAsync(DbConnection connection, DbTransaction transaction, TableName name, CancellationToken cancellationToken)
    {
        const string sql = "SELECT column_name, data_type, is_nullable FROM information_schema.columns WHERE table_schema = @schema AND table_name = @table ORDER BY ordinal_position";
        List<ColumnShape> columns = [];

        await using var command = Command(connection, transaction, sql, 30);
        Parameter(command, "@schema", name.Schema);
        Parameter(command, "@table", name.Table);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            columns.Add(new ColumnShape(reader.GetString(0), [reader.GetString(1)], string.Equals(reader.GetString(2), "YES", StringComparison.OrdinalIgnoreCase)));

        return columns;
    }

    private static async Task<Dictionary<string, string>> ExamplesAsync(SourceKind kind, DbConnection connection, DbTransaction transaction, TableName name, CancellationToken cancellationToken)
    {
        string sql = kind is SourceKind.PostgreSql
            ? $"SELECT * FROM {Quoted(kind, name)} LIMIT {Sample}"
            : $"SELECT TOP ({Sample}) * FROM {Quoted(kind, name)}";
        var examples = new Dictionary<string, string>(StringComparer.Ordinal);

        await using var command = Command(connection, transaction, sql, 30);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            for (int index = 0; index < reader.FieldCount; index++)
            {
                if (!await reader.IsDBNullAsync(index, cancellationToken) && !examples.ContainsKey(reader.GetName(index)))
                    examples[reader.GetName(index)] = Shortened(Value(reader.GetValue(index))?.ToJsonString() ?? string.Empty);
            }
        }

        return examples;
    }

    private static async Task<long> EstimateAsync(SourceKind kind, DbConnection connection, DbTransaction transaction, TableName name, CancellationToken cancellationToken)
    {
        string sql = kind is SourceKind.PostgreSql
            ? "SELECT greatest(c.reltuples, 0)::bigint FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = @schema AND c.relname = @table"
            : "SELECT CAST(SUM(p.rows) AS bigint) FROM sys.partitions p JOIN sys.tables t ON t.object_id = p.object_id JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = @schema AND t.name = @table AND p.index_id IN (0, 1)";

        await using var command = Command(connection, transaction, sql, 30);
        Parameter(command, "@schema", name.Schema);
        Parameter(command, "@table", name.Table);
        object? counted = await command.ExecuteScalarAsync(cancellationToken);

        return counted is null or DBNull ? 0 : Convert.ToInt64(counted, CultureInfo.InvariantCulture);
    }

    private static async Task<IReadOnlyList<string>> IndexesAsync(SourceKind kind, DbConnection connection, DbTransaction transaction, TableName name, CancellationToken cancellationToken)
    {
        string sql = kind is SourceKind.PostgreSql
            ? "SELECT indexdef FROM pg_indexes WHERE schemaname = @schema AND tablename = @table ORDER BY indexname"
            : "SELECT i.name + ' (' + STRING_AGG(c.name, ', ') WITHIN GROUP (ORDER BY ic.key_ordinal) + ')' FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id JOIN sys.tables t ON t.object_id = i.object_id JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = @schema AND t.name = @table AND i.name IS NOT NULL GROUP BY i.name ORDER BY i.name";
        List<string> indexes = [];

        await using var command = Command(connection, transaction, sql, 30);
        Parameter(command, "@schema", name.Schema);
        Parameter(command, "@table", name.Table);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            indexes.Add(reader.GetString(0));

        return indexes;
    }

    private static string Quoted(SourceKind kind, TableName name) => kind is SourceKind.PostgreSql
        ? $"\"{name.Schema.Replace("\"", "\"\"", StringComparison.Ordinal)}\".\"{name.Table.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
        : $"[{name.Schema.Replace("]", "]]", StringComparison.Ordinal)}].[{name.Table.Replace("]", "]]", StringComparison.Ordinal)}]";

    private static string Shortened(string text) => text.Length <= ExampleLength ? text : $"{text[..ExampleLength]}…";

    private static async Task<Answer<T>> GuardedAsync<T>(ReachableSource source, int seconds, Func<DbConnection, DbTransaction, Task<T>> read, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = Connection(source);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await ReadOnlyAsync(source.Source.Kind, connection, cancellationToken);

            T answer = await read(connection, transaction);
            await transaction.RollbackAsync(cancellationToken);

            return Answer.Ok(answer);
        }
        catch (Exception exception) when (IsTimeout(exception))
        {
            return Answer.Failed<T>(Messages.TooSlow(seconds), Messages.TooSlowHint);
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException or ArgumentException or TimeoutException)
        {
            return Answer.Failed<T>(Messages.DatabaseSaid(source.Source.Name, Scrubbed(exception.Message)), Messages.SelectHint);
        }
    }

    private static bool IsTimeout(Exception exception) =>
        exception is SqlException { Number: -2 } or PostgresException { SqlState: "57014" }
        || exception.InnerException is TimeoutException;

    [GeneratedRegex(@"(?i)password\s*=\s*[^;]*")]
    private static partial Regex Password();

    /// <summary>A table by its schema and name as the database spells them.</summary>
    private sealed record TableName(string Schema, string Table);

    /// <summary>A column, its type and whether it may be empty.</summary>
    private sealed record ColumnShape(string Name, IReadOnlyList<string> Types, bool IsNullable);
}
