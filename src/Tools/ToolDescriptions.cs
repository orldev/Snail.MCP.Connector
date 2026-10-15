namespace Snail.MCP.Connector.Tools;

/// <summary>What the model reads before calling: the tool contract, guarded by the tests in <c>ToolCatalogTests</c>.</summary>
/// <remarks>Every number here is a default of <c>ConnectorOptions</c>, and a guard test fails when the two drift apart.</remarks>
public static class ToolDescriptions
{
    /// <summary>The engines a source runs on, as the tools spell them; a guard test compares them with <c>SourceKind</c>.</summary>
    public const string Kinds = "mongodb, sqlserver or postgresql";

    /// <summary>The formats an export writes; a guard test compares them with <c>TableFormat</c>.</summary>
    public const string Formats = "csv or xlsx";

    /// <summary>How a query is written.</summary>
    private const string ExtendedJson = """MongoDB Extended JSON, such as {"status": "paid", "total": {"$gt": 100}}; dates as {"$date": "2026-01-31T00:00:00Z"}, ids as {"$oid": "…"}""";

    /// <summary>The contract of connector_sources.</summary>
    public const string Sources =
        $"""
        List the company databases declared on the stand: each source's name, engine ({Kinds}), database, the collections
        an agent may read and what a person wrote about it. Start here, then read a source's shape with connector_schema.
        """;

    /// <summary>The contract of connector_schema.</summary>
    public const string Schema =
        """
        Read what a source holds before querying it. Without a collection: the collections an agent may read. With one:
        its fields read from a sample of 200 documents — each field's dotted path, its types, how often it is present and
        an example value — the documents it holds and its indexes, which are what a filter is quick on.
        """;

    /// <summary>The contract of connector_find.</summary>
    public const string Find =
        $"""
        Read documents of a collection that match a filter, with the fields and order asked for. Write the filter,
        projection and sort as {ExtendedJson}. An answer carries 50 documents unless asked for more, at most 200, and at
        most 65536 bytes of them; when it was cut it says so — narrow the filter or count first. A query gets 30 seconds.
        Only reads: operators that write or run code are refused.
        """;

    /// <summary>The contract of connector_aggregate.</summary>
    public const string Aggregate =
        $"""
        Run a MongoDB aggregation pipeline on a collection to group, sum, count and join — the way to analyse rather than
        list. Write the pipeline as a JSON array of stages in {ExtendedJson}. $lookup, $graphLookup and $unionWith may
        read only collections the source allows; $out, $merge, $function, $accumulator and $where are refused. The answer
        carries 50 rows unless asked for more, at most 200, at most 65536 bytes; a query gets 30 seconds.
        """;

    /// <summary>The contract of connector_count.</summary>
    public const string Count =
        $"""
        Count the documents of a collection that match a filter, written as {ExtendedJson}; an empty filter counts them
        all. Cheaper than reading them: count before reading a large collection.
        """;

    /// <summary>The contract of connector_export.</summary>
    public const string Export =
        $"""
        Write the documents of a filter, or the rows of a pipeline, into a {Formats} file of the task, under exports/ of
        the working copy, so a person gets the data as a table. Give either a filter or a pipeline. Nested fields become
        columns of their dotted paths. An export writes at most 100000 rows and says when it stopped there.
        """;

    /// <summary>The contract of connector_journal.</summary>
    public const string Journal =
        """
        Read the queries this task already made — which source and collection, what was asked, how many rows came back,
        whether it failed — newest first, so a query is not repeated. Every query is written there, for the company to see.
        """;

    /// <summary>The operations a write proposes; a guard test compares them with <c>WriteOperation</c>.</summary>
    public const string Operations = "update, update_one, insert or delete";

    /// <summary>The contract of connector_propose_write.</summary>
    public const string ProposeWrite =
        $"""
        Propose a change to a company database — {Operations} — when the task needs one. Nothing is written: the server
        dry-runs it as the read-only user, counts the documents it touches and keeps a few of them as they are now, and
        answers a proposal id. Then call request_write_approval with server "connector" and that proposal: the task waits
        until the stand's owner reads the proposal and allows or refuses it. An update or a delete needs a filter naming
        the documents; an update is field operators such as $set; one proposal touches at most 1000 documents. The source
        must have a writing user.
        """;

    /// <summary>The contract of connector_apply.</summary>
    public const string Apply =
        """
        Run a proposal the stand's owner allowed, once, as the source's writing user, and get back how many documents it
        changed. Only the task that proposed it may run it, and only within 7 days; a proposal nobody allowed yet, refused,
        or already run is refused with what to do.
        """;

    /// <summary>The operation parameter.</summary>
    public const string Operation = $"What the write does: {Operations}.";

    /// <summary>The filter parameter of connector_propose_write.</summary>
    public const string WriteFilter = """Which documents an update or a delete touches, such as {"number": "2026-0042"}; it may not be empty. Not used by an insert.""";

    /// <summary>The change parameter of connector_propose_write.</summary>
    public const string Change = """For an update, the field operators, such as {"$set": {"status": "paid"}}; for an insert, a JSON array of the documents; not used by a delete.""";

    /// <summary>The reason parameter of connector_propose_write.</summary>
    public const string Reason = "Why the task needs this write, in one or two sentences for the person who decides.";

    /// <summary>The proposal parameter of connector_apply.</summary>
    public const string Proposal = "The proposal id connector_propose_write answered.";

    /// <summary>The contract of connector_diagnose.</summary>
    public const string Diagnose = "Say whether the server can reach the stand's sources: its version, Vault, and how many sources are declared. Changes nothing.";

    /// <summary>The source parameter.</summary>
    public const string SourceName = "The source's name, as connector_sources lists it.";

    /// <summary>The collection parameter.</summary>
    public const string Collection = "The collection, as connector_schema lists it.";

    /// <summary>The collection parameter of connector_schema.</summary>
    public const string SchemaCollection = "The collection whose fields to read; without one, the collections an agent may read.";

    /// <summary>The filter parameter.</summary>
    public const string Filter = $"Which documents, as {ExtendedJson}; empty matches every one.";

    /// <summary>The projection parameter.</summary>
    public const string Projection = """Which fields to return, such as {"number": 1, "total": 1, "_id": 0}; empty returns them all.""";

    /// <summary>The sort parameter.</summary>
    public const string Sort = """The order, such as {"date": -1}; empty leaves the database's order.""";

    /// <summary>The limit parameter.</summary>
    public const string Limit = "How many rows to return: 50 when not given, at most 200.";

    /// <summary>The pipeline parameter.</summary>
    public const string Pipeline = """The stages as a JSON array, such as [{"$match": {"status": "paid"}}, {"$group": {"_id": "$customer", "total": {"$sum": "$total"}}}].""";

    /// <summary>The pipeline parameter of connector_export.</summary>
    public const string ExportPipeline = "The stages as a JSON array, when the rows come from a pipeline rather than a filter.";

    /// <summary>The format parameter.</summary>
    public const string Format = $"The file's format: {Formats}.";

    /// <summary>The file parameter.</summary>
    public const string File = "The file's name, letters, digits, dots, dashes and underscores; the format's extension is added when missing.";

    /// <summary>The most parameter of connector_journal.</summary>
    public const string JournalMost = "How many queries to return, newest first: at most 200.";
}
