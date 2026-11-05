using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Npgsql;
using NpgsqlTypes;
using Snail.MCP.Connector.Application;
using Microsoft.Extensions.Options;
using Snail.MCP.Connector.Application.Ports;
using Snail.MCP.Connector.Configuration;

namespace Snail.MCP.Connector.Adapters;

/// <summary>PostgreSQL and SQL Server read by the source's read-only user, inside a transaction that is always rolled back.</summary>
/// <remarks>
/// Three walls: the user cannot write, PostgreSQL's transaction is READ ONLY so the database refuses a write itself, and
/// both are rolled back whatever ran in them. On PostgreSQL the statement is read as a subquery with a LIMIT, so it is
/// one statement whatever its text holds — a COMMIT behind it would have ended the read-only transaction (audit of
/// 30.10.2026) — and the database stops at the limit itself. The database stops a statement at the query's seconds (its
/// command timeout). Rows are read one more than asked, so an answer knows it was cut; on SQL Server the command is
/// cancelled there, as a reader closed without it pulls the rest of the result over the network — 10.4 s for a page of
/// one row of a hundred million, measured on PostgreSQL before its LIMIT.
/// </remarks>
public sealed partial class SqlTables(IOptions<ConnectorOptions> options) : ITables
{
    /// <summary>How long one call to the database may run, from QUERY_SECONDS.</summary>
    private int Seconds => options.Value.QuerySeconds;

    private const int Sample = 3;

    private const int ExampleLength = 60;

    /// <inheritdoc />
    public async Task<Answer<IReadOnlyList<string>>> TablesAsync(ReachableSource source, CancellationToken cancellationToken) =>
        await GuardedAsync(source, Seconds, async (connection, transaction) =>
        {
            const string sql = "SELECT table_schema, table_name FROM information_schema.tables WHERE table_schema NOT IN ('pg_catalog', 'information_schema', 'sys') ORDER BY table_schema, table_name";
            List<string> tables = [];

            await using var command = Command(connection, transaction, sql, Seconds);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                tables.Add($"{reader.GetString(0)}.{reader.GetString(1)}");

            return (IReadOnlyList<string>)tables;
        }, cancellationToken);

    /// <inheritdoc />
    public async Task<Answer<CollectionShape>> ShapeAsync(ReachableSource source, string table, CancellationToken cancellationToken) =>
        await GuardedAsync(source, Seconds, async (connection, transaction) =>
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

            await using var command = Command(connection, transaction, Bounded(source.Source.Kind, select, limit.Rows + 1), limit.Seconds);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                string row = Row(reader);
                bytes += Encoding.UTF8.GetByteCount(row);

                if (rows.Count == limit.Rows || bytes > limit.Bytes)
                {
                    await StopAsync(source.Source.Kind, command, reader);
                    return new RowPage(rows, IsCut: true);
                }

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

        await using (var command = Command(connection, transaction, Bounded(source.Source.Kind, select, most + 1), options.Value.ExportSeconds))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            int read = 0;
            while (read <= most && await reader.ReadAsync(cancellationToken))
            {
                read++;
                yield return Row(reader);
            }

            if (read > most)
                await StopAsync(source.Source.Kind, command, reader);
        }

        await RolledBackAsync(transaction, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Answer<WritePreview>> PreviewAsync(ReachableSource source, string table, string filter, int sample, CancellationToken cancellationToken) =>
        await GuardedAsync(source, Seconds, async (connection, transaction) =>
        {
            string name = Quoted(source.Source.Kind, await ResolvedAsync(connection, transaction, table, cancellationToken));
            var matching = JsonNode.Parse(filter)!.AsObject();

            await using var count = Bound(connection, transaction, SqlStatements.Count(source.Source.Kind, name, matching), Seconds);
            long matched = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);

            List<string> shown = [];
            await using var rows = Bound(connection, transaction, SqlStatements.Sample(source.Source.Kind, name, matching, sample), Seconds);
            await using var reader = await rows.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                shown.Add(Row(reader));

            return new WritePreview(matched, shown);
        }, cancellationToken);

    /// <inheritdoc />
    /// <remarks>By the writing user, in one transaction committed only when the rows written are no more than the person saw.</remarks>
    public async Task<Answer<long>> WriteAsync(ReachableSource source, WriteProposal proposal, CancellationToken cancellationToken)
    {
        if (source.Access.WriteUri is not { } uri)
            return Answer.Failed<long>(Messages.NoWriter, Messages.NoWriterHint);

        var kind = source.Source.Kind;
        try
        {
            await using var connection = Connection(kind, uri, source.Source.Database);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

            string name = Quoted(kind, await ResolvedAsync(connection, transaction, proposal.Collection, cancellationToken));
            long written = 0;
            foreach (var statement in SqlStatements.Write(kind, name, proposal))
            {
                await using var command = Bound(connection, transaction, statement, Seconds);
                written += await command.ExecuteNonQueryAsync(cancellationToken);
            }

            if (proposal.Operation is not WriteOperation.Insert && written > proposal.Matched)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Answer.Failed<long>(Messages.Changed(written, proposal.Matched));
            }

            await transaction.CommitAsync(cancellationToken);

            return Answer.Ok(written);
        }
        catch (Exception exception) when (IsTimeout(exception))
        {
            return Answer.Failed<long>(Messages.TooSlow(Seconds), Messages.TooSlowHint);
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException or ArgumentException or TimeoutException)
        {
            return Answer.Failed<long>(Messages.DatabaseSaid(source.Source.Name, Scrubbed(exception.Message)));
        }
    }

    /// <summary>The message of a failure without a password a connection string may carry.</summary>
    public static string Scrubbed(string message) => Password().Replace(MongoDocuments.Scrubbed(message), "Password=***");

    /// <summary>A row as one compact JSON object, its values as a person reads them.</summary>
    public static string Row(DbDataReader reader)
    {
        var row = new JsonObject();

        for (int index = 0; index < reader.FieldCount; index++)
            row[reader.GetName(index)] = Cell(reader, index);

        return row.ToJsonString();
    }

    /// <summary>The statement as the engine runs it: on PostgreSQL a subquery cut at <paramref name="rows"/>, on SQL Server as written.</summary>
    /// <remarks>The text goes inside on lines of its own, so a comment at its end cannot swallow the closing parenthesis.</remarks>
    public static string Bounded(SourceKind kind, string select, int rows) => kind is SourceKind.PostgreSql
        ? string.Create(CultureInfo.InvariantCulture, $"SELECT * FROM (\n{select}\n) AS q LIMIT {rows}")
        : select;

    /// <summary>One value of the row; one .NET cannot hold — an interval of months, a NaN, a number past decimal — as the engine spells it.</summary>
    /// <remarks>
    /// <c>age()</c>, the first thing an analysis of overdue invoices writes, answers an interval of months, which Npgsql will
    /// not read as a TimeSpan; the exception it threw ended the tool call with no answer and no journal line.
    /// </remarks>
    private static JsonNode? Cell(DbDataReader reader, int index)
    {
        if (reader.IsDBNull(index))
            return null;

        try
        {
            return Value(reader.GetValue(index));
        }
        catch (Exception exception) when (exception is InvalidCastException or OverflowException)
        {
            return JsonValue.Create(Spelled(reader, index));
        }
    }

    private static string Spelled(DbDataReader reader, int index)
    {
        try
        {
            return reader switch
            {
                NpgsqlDataReader postgres when postgres.GetDataTypeName(index) == "interval" => Spoken(postgres.GetFieldValue<NpgsqlInterval>(index)),
                NpgsqlDataReader postgres => postgres.GetFieldValue<double>(index).ToString("R", CultureInfo.InvariantCulture),
                _ => Convert.ToString(reader.GetProviderSpecificValue(index), CultureInfo.InvariantCulture) ?? string.Empty
            };
        }
        catch (Exception exception) when (exception is InvalidCastException or OverflowException or NotSupportedException)
        {
            return $"<{reader.GetDataTypeName(index)}>";
        }
    }

    private static string Spoken(NpgsqlInterval interval) =>
        string.Create(CultureInfo.InvariantCulture, $"{interval.Months} months {interval.Days} days {TimeSpan.FromTicks(interval.Time * 10):c}");

    /// <summary>Stops a result read no further: on SQL Server the command is cancelled, so closing the reader does not pull the rest of it over the network.</summary>
    /// <remarks>
    /// PostgreSQL stopped at the LIMIT itself and has nothing left to send; a cancel there finds no statement running and
    /// Npgsql waits out its cancellation timeout for an answer that does not come — 6.3 s for a page of one row.
    /// </remarks>
    private static async Task StopAsync(SourceKind kind, DbCommand command, DbDataReader reader)
    {
        if (kind is not SourceKind.SqlServer)
            return;

        command.Cancel();

        try
        {
            await reader.DisposeAsync();
        }
        catch (DbException exception) when (IsTimeout(exception) || exception is SqlException)
        {
            return;
        }
    }

    /// <summary>Rolls back a reading transaction; one the cancelled command already ended has nothing left to roll back.</summary>
    private static async Task RolledBackAsync(DbTransaction transaction, CancellationToken cancellationToken)
    {
        try
        {
            await transaction.RollbackAsync(cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return;
        }
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

    private static DbConnection Connection(ReachableSource source) => Connection(source.Source.Kind, source.Access.ReadUri, source.Source.Database);

    private static DbConnection Connection(SourceKind kind, string uri, string database) => kind switch
    {
        SourceKind.PostgreSql => new NpgsqlConnection(new NpgsqlConnectionStringBuilder(uri) { Database = database, ApplicationName = "snail-mcp-connector" }.ConnectionString),
        _ => new SqlConnection(new SqlConnectionStringBuilder(uri) { InitialCatalog = database, ApplicationName = "snail-mcp-connector" }.ConnectionString)
    };

    /// <summary>A transaction the statement runs in and that is never committed; PostgreSQL's also refuses any write itself.</summary>
    private async Task<DbTransaction> ReadOnlyAsync(SourceKind kind, DbConnection connection, CancellationToken cancellationToken)
    {
        var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        if (kind is not SourceKind.PostgreSql)
            return transaction;

        await using var command = Command(connection, transaction, "SET TRANSACTION READ ONLY", Seconds);
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

    /// <summary>A command of a statement the server wrote, its values bound; PostgreSQL takes text as untyped, so the column's type decides.</summary>
    private static DbCommand Bound(DbConnection connection, DbTransaction transaction, SqlStatement statement, int seconds)
    {
        var command = Command(connection, transaction, statement.Text, seconds);
        for (int index = 0; index < statement.Values.Count; index++)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = $"@p{index.ToString(CultureInfo.InvariantCulture)}";
            parameter.Value = Plain(statement.Values[index]);
            if (parameter is NpgsqlParameter untyped && IsText(statement.Values[index]))
                untyped.NpgsqlDbType = NpgsqlDbType.Unknown;

            command.Parameters.Add(parameter);
        }

        return command;
    }

    private static bool IsText(JsonNode? value) => value is null || value.GetValueKind() is JsonValueKind.String;

    private static object Plain(JsonNode? value) => value?.GetValueKind() switch
    {
        null or JsonValueKind.Null => DBNull.Value,
        JsonValueKind.String => value.GetValue<string>(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ when value.AsValue().TryGetValue(out long whole) => whole,
        _ when value.AsValue().TryGetValue(out decimal number) => number,
        _ => value.GetValue<double>()
    };

    private static void Parameter(DbCommand command, string name, string value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    /// <summary>The table's schema and name as the database spells them; a bare name is looked up, the usual schema first.</summary>
    private async Task<TableName> ResolvedAsync(DbConnection connection, DbTransaction transaction, string table, CancellationToken cancellationToken)
    {
        string[] parts = table.Split('.', 2);
        const string sql = "SELECT table_schema, table_name FROM information_schema.tables WHERE lower(table_name) = lower(@table) AND (@schema = '' OR lower(table_schema) = lower(@schema)) ORDER BY CASE WHEN table_schema IN ('public', 'dbo') THEN 0 ELSE 1 END, table_schema";

        await using var command = Command(connection, transaction, sql, Seconds);
        Parameter(command, "@table", parts[^1]);
        Parameter(command, "@schema", parts.Length == 2 ? parts[0] : string.Empty);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException($"no table is called {table}");

        return new TableName(reader.GetString(0), reader.GetString(1));
    }

    private async Task<IReadOnlyList<ColumnShape>> ColumnsAsync(DbConnection connection, DbTransaction transaction, TableName name, CancellationToken cancellationToken)
    {
        const string sql = "SELECT column_name, data_type, is_nullable FROM information_schema.columns WHERE table_schema = @schema AND table_name = @table ORDER BY ordinal_position";
        List<ColumnShape> columns = [];

        await using var command = Command(connection, transaction, sql, Seconds);
        Parameter(command, "@schema", name.Schema);
        Parameter(command, "@table", name.Table);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            columns.Add(new ColumnShape(reader.GetString(0), [reader.GetString(1)], string.Equals(reader.GetString(2), "YES", StringComparison.OrdinalIgnoreCase)));

        return columns;
    }

    private async Task<Dictionary<string, string>> ExamplesAsync(SourceKind kind, DbConnection connection, DbTransaction transaction, TableName name, CancellationToken cancellationToken)
    {
        string sql = kind is SourceKind.PostgreSql
            ? $"SELECT * FROM {Quoted(kind, name)} LIMIT {Sample}"
            : $"SELECT TOP ({Sample}) * FROM {Quoted(kind, name)}";
        var examples = new Dictionary<string, string>(StringComparer.Ordinal);

        await using var command = Command(connection, transaction, sql, Seconds);
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

    private async Task<long> EstimateAsync(SourceKind kind, DbConnection connection, DbTransaction transaction, TableName name, CancellationToken cancellationToken)
    {
        string sql = kind is SourceKind.PostgreSql
            ? "SELECT greatest(c.reltuples, 0)::bigint FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = @schema AND c.relname = @table"
            : "SELECT CAST(SUM(p.rows) AS bigint) FROM sys.partitions p JOIN sys.tables t ON t.object_id = p.object_id JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = @schema AND t.name = @table AND p.index_id IN (0, 1)";

        await using var command = Command(connection, transaction, sql, Seconds);
        Parameter(command, "@schema", name.Schema);
        Parameter(command, "@table", name.Table);
        object? counted = await command.ExecuteScalarAsync(cancellationToken);

        return counted is null or DBNull ? 0 : Convert.ToInt64(counted, CultureInfo.InvariantCulture);
    }

    private async Task<IReadOnlyList<string>> IndexesAsync(SourceKind kind, DbConnection connection, DbTransaction transaction, TableName name, CancellationToken cancellationToken)
    {
        string sql = kind is SourceKind.PostgreSql
            ? "SELECT indexdef FROM pg_indexes WHERE schemaname = @schema AND tablename = @table ORDER BY indexname"
            : "SELECT i.name + ' (' + STRING_AGG(c.name, ', ') WITHIN GROUP (ORDER BY ic.key_ordinal) + ')' FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id JOIN sys.tables t ON t.object_id = i.object_id JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = @schema AND t.name = @table AND i.name IS NOT NULL GROUP BY i.name ORDER BY i.name";
        List<string> indexes = [];

        await using var command = Command(connection, transaction, sql, Seconds);
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

    private async Task<Answer<T>> GuardedAsync<T>(ReachableSource source, int seconds, Func<DbConnection, DbTransaction, Task<T>> read, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = Connection(source);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await ReadOnlyAsync(source.Source.Kind, connection, cancellationToken);

            T answer = await read(connection, transaction);
            await RolledBackAsync(transaction, cancellationToken);

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
