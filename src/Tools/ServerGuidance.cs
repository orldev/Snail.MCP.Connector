namespace Snail.MCP.Connector.Tools;

/// <summary>What the model is told about this server when it connects.</summary>
public static class ServerGuidance
{
    /// <summary>The instructions a client shows the model before any tool.</summary>
    public const string Instructions =
        """
        This server reads the company's databases for the task: search, analysis and tables for people. Every query runs
        as a database user that cannot write and is written into a journal the company reads. A change to the data is
        never made by a query: it is proposed, and it runs only after the stand's owner or an administrator allows it.

        Start with connector_sources, then connector_schema for a source and for the collections or tables you need: the
        fields or columns, their types and the indexes. A MongoDB source is read with connector_find, connector_count and
        connector_aggregate; a SQL source with connector_query and one SELECT. Analyse by grouping and summing in the
        database rather than reading every row and adding them up yourself. When a person needs the data as a table, write
        it with connector_export and name the file in your answer. A change to the data is proposed with
        connector_propose_write and runs only after the stand's owner or an administrator allows it.

        Every answer is JSON with an ok flag. When ok is false nothing happened: read error and hint.
        """;
}
