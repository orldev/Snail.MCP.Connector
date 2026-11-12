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
        connector_propose_write and runs only after the stand's owner or an administrator allows it. find_tool names the tool
        for a task in your words; several queries known in advance go in one call of run_tools, and queries that depend on
        each other's answers in one run_program (connector.find({...}) calls connector_find): each is journaled all the same.

        Every answer is JSON with an ok flag, and a failed call is also flagged isError. When ok is false, read error and
        hint and fix the call instead of retrying it verbatim; type names the failure: arguments a call to fix, notAllowed
        what this server or the source never runs, pending, rejected, applied or expired a proposal's state, journalDown
        or notConfigured the stand's to fix, databaseError or timeout the database's. An export whose job failed after it
        started answers ok false as well: it was begun in the journal, and no file was left in the working copy.
        """;
}
