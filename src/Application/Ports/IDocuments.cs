namespace Snail.MCP.Connector.Application.Ports;

/// <summary>A document database read by its read-only user.</summary>
/// <remarks>Every call answers in data: a refused login, a bad query or a query stopped by its time come back as words, not as exceptions.</remarks>
public interface IDocuments
{
    /// <summary>The collections the database holds.</summary>
    Task<Answer<IReadOnlyList<string>>> CollectionsAsync(ReachableSource source, CancellationToken cancellationToken);

    /// <summary>The collection's fields, read from a sample of its documents, with its indexes.</summary>
    Task<Answer<CollectionShape>> ShapeAsync(ReachableSource source, string collection, int sample, CancellationToken cancellationToken);

    /// <summary>The documents a filter matches, as far as the limit and the size let.</summary>
    Task<Answer<RowPage>> FindAsync(ReachableSource source, DocumentQuery query, CancellationToken cancellationToken);

    /// <summary>What an aggregation pipeline answers, as far as the limit and the size let.</summary>
    Task<Answer<RowPage>> AggregateAsync(ReachableSource source, string collection, string pipeline, RowLimit limit, CancellationToken cancellationToken);

    /// <summary>How many documents a filter matches.</summary>
    Task<Answer<long>> CountAsync(ReachableSource source, string collection, string filter, CancellationToken cancellationToken);

    /// <summary>How many documents a write's filter matches, and a few of them as they are now: the dry run, by the read-only user.</summary>
    Task<Answer<WritePreview>> PreviewAsync(ReachableSource source, string collection, string filter, int sample, CancellationToken cancellationToken);

    /// <summary>Runs an allowed write by the source's writing user; answers how many documents it changed.</summary>
    Task<Answer<long>> WriteAsync(ReachableSource source, WriteProposal proposal, CancellationToken cancellationToken);

    /// <summary>Every row of a filter or a pipeline, up to <paramref name="most"/>, one at a time for a file to take them.</summary>
    IAsyncEnumerable<string> StreamAsync(ReachableSource source, DocumentQuery query, string? pipeline, int most, CancellationToken cancellationToken);
}

/// <summary>A filter query of one collection.</summary>
/// <param name="Collection">The collection.</param>
/// <param name="Filter">The filter as Extended JSON; <c>{}</c> matches every document.</param>
/// <param name="Projection">Which fields to return, as Extended JSON; null returns them all.</param>
/// <param name="Sort">The order, as Extended JSON; null leaves the database's.</param>
/// <param name="Limit">How much one answer may carry.</param>
public sealed record DocumentQuery(string Collection, string Filter, string? Projection, string? Sort, RowLimit Limit);

/// <summary>How many rows, and how many bytes of them, one answer carries at most; and how long the query may take.</summary>
/// <param name="Rows">Rows at most.</param>
/// <param name="Bytes">Bytes of rows at most.</param>
/// <param name="Seconds">Seconds the database gets.</param>
public sealed record RowLimit(int Rows, int Bytes, int Seconds);

/// <summary>What a write's filter matches now.</summary>
/// <param name="Matched">How many documents.</param>
/// <param name="Sample">A few of them as relaxed Extended JSON.</param>
public sealed record WritePreview(long Matched, IReadOnlyList<string> Sample);
