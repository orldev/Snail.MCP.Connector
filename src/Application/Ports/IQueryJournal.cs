namespace Snail.MCP.Connector.Application.Ports;

/// <summary>Where every query is written down.</summary>
public interface IQueryJournal
{
    /// <summary>Writes the query down, or says why it could not: an answer whose query is not written down is withheld.</summary>
    Task<Answer<QueryRecord>> WriteAsync(QueryRecord query, CancellationToken cancellationToken);

    /// <summary>The newest queries of a task, newest first.</summary>
    Task<IReadOnlyList<QueryRecord>> ReadAsync(string task, int most, CancellationToken cancellationToken);
}
