namespace Snail.MCP.Connector.Tools;

/// <summary>What the model is told about this server when it connects.</summary>
public static class ServerGuidance
{
    /// <summary>The instructions a client shows the model before any tool.</summary>
    public const string Instructions =
        """
        This server reads the company's databases for the task: search, analysis and tables for people. It only reads, as a
        database user that cannot write, and every query is written into a journal the company reads.

        Start with connector_sources, then connector_schema for a source and for the collections you need: the fields,
        their types and the indexes. Count before reading a large collection. Analyse with connector_aggregate — group,
        sum, count — rather than reading every document and adding them up yourself. When a person needs the data as a
        table, write it with connector_export and name the file in your answer.

        Every answer is JSON with an ok flag. When ok is false nothing happened: read error and hint.
        """;
}
