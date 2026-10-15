namespace Snail.MCP.Connector.Application.Ports;

/// <summary>A SQL database — PostgreSQL or SQL Server — read by its read-only user inside a transaction that cannot write.</summary>
/// <remarks>Every call answers in data: a refused login, a bad statement or one stopped by its time come back as words, not as exceptions.</remarks>
public interface ITables
{
    /// <summary>The tables and views the database holds, as <c>schema.name</c>.</summary>
    Task<Answer<IReadOnlyList<string>>> TablesAsync(ReachableSource source, CancellationToken cancellationToken);

    /// <summary>The table's columns with their types, the rows it holds by the database's estimate, its indexes and an example of each column.</summary>
    Task<Answer<CollectionShape>> ShapeAsync(ReachableSource source, string table, CancellationToken cancellationToken);

    /// <summary>The rows a SELECT answers, as far as the limit and the size let.</summary>
    Task<Answer<RowPage>> QueryAsync(ReachableSource source, string select, RowLimit limit, CancellationToken cancellationToken);

    /// <summary>Every row of a SELECT, up to one past <paramref name="most"/>, one at a time for a file to take them.</summary>
    IAsyncEnumerable<string> StreamAsync(ReachableSource source, string select, int most, CancellationToken cancellationToken);
}
