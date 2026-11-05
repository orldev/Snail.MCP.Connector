namespace Snail.MCP.Connector.Application;

/// <summary>What a caller reads when something did not happen, with what to do about it.</summary>
public static class Messages
{
    /// <summary>The session names no working copy.</summary>
    public const string NoCopy = "This session names no working copy, so no query of it can be written into the journal under a task.";

    /// <summary>Where the copy comes from.</summary>
    public const string NoCopyHint = "The platform names the step's copy in the server's address as ?root=; connect the server to a role through the platform.";

    /// <summary>Vault is not configured.</summary>
    public const string NoVault = "No source is declared: the server has no Vault address to read the sources from.";

    /// <summary>How to configure it.</summary>
    public const string NoVaultHint = "Set SNAIL_MCP_CONNECTOR_VAULT_ADDRESS and SNAIL_MCP_CONNECTOR_VAULT_TOKEN, then declare a source on the stand.";

    /// <summary>No source was declared yet.</summary>
    public const string NoSources = "No source is declared on the stand yet.";

    /// <summary>How to declare one.</summary>
    public const string NoSourcesHint = "A person declares a company database on the stand; this server cannot add one itself.";

    /// <summary>The source is not a document database.</summary>
    public const string NotDocuments = "This source is a SQL database: read it with connector_query and a SELECT; filters and pipelines are for MongoDB sources.";

    /// <summary>A MongoDB source asked with SQL.</summary>
    public const string NotTables = "This source is a MongoDB database: read it with connector_find, connector_aggregate and connector_count; connector_query is for SQL sources.";

    /// <summary>A SQL filter that is not columns and values.</summary>
    public static string SqlFilterBad(string detail) => $"The filter cannot be read: {detail}.";

    /// <summary>How a SQL filter is written.</summary>
    public const string SqlFilterHint = "Name the rows by columns, such as {\"id\": 42} or {\"status\": \"open\", \"due\": {\"$lt\": \"2026-06-01\"}}: a value means equal; $eq, $ne, $gt, $gte, $lt, $lte, $in (a list) and $null (true or false) compare; every condition must hold.";

    /// <summary>A column of a SQL update or insert that is not a plain name with a plain value.</summary>
    public static string SqlColumnBad(string column) => $"\"{column}\" is not a column with a plain value: names are letters, digits and _, values are text, numbers, true, false or null.";

    /// <summary>How a SQL update is written.</summary>
    public const string SqlUpdateHint = "Write the update as {\"$set\": {\"status\": \"paid\"}} or {\"$inc\": {\"stock\": -1}}: $set gives columns values, $inc adds a number to numeric columns.";

    /// <summary>How a SQL insert is written.</summary>
    public const string SqlInsertHint = "Write the rows as a JSON array of objects, such as [{\"id\": 301, \"client_id\": 7, \"amount\": 120.5}].";

    /// <summary>A table name that is not one or two plain names.</summary>
    public static string SqlTableBad(string table) => $"\"{table}\" is not a table name: write it as name or schema.name, letters, digits and _.";

    /// <summary>An update_one on SQL whose filter does not name exactly one row.</summary>
    public static string NotOne(long matched) => $"update_one changes exactly one row, and the filter matches {matched}.";

    /// <summary>What to do about it.</summary>
    public const string NotOneHint = "Name the row by its key, such as {\"id\": 42}, or use update for every row the filter matches.";

    /// <summary>A write that would touch more rows at apply than the person saw.</summary>
    public static string Changed(long written, long matched) =>
        $"The data changed since the proposal: the write would touch {written}, the person allowed {matched}. Nothing was written; propose it again.";

    /// <summary>A statement that is not a SELECT.</summary>
    public const string NotASelect = "The statement is not a SELECT: this server runs one SELECT, or a WITH that ends in one.";

    /// <summary>More than one statement.</summary>
    public const string OneStatement = "The statement holds more than one: send one SELECT at a time.";

    /// <summary>How a SQL query is written.</summary>
    public const string SelectHint = "Write one SELECT, such as SELECT city, count(*) FROM customers GROUP BY city; read the tables and columns with connector_schema first.";

    /// <summary>A word that writes or changes the database.</summary>
    public static string SqlForbidden(string word) => $"{word} is not run by this server: it writes or changes the database. Only SELECT reads are run.";

    /// <summary>The pipeline is not an array of stages.</summary>
    public const string NotAPipeline = "The pipeline is not a JSON array of stage objects.";

    /// <summary>How queries are written.</summary>
    public const string ExtendedJsonHint = """Write it as MongoDB Extended JSON: {"status": "paid", "total": {"$gt": 100}}, dates as {"$date": "2026-01-31T00:00:00Z"}, ids as {"$oid": "…"}.""";

    /// <summary>What the server does not do.</summary>
    public const string ReadOnlyHint = "A query only reads: no stage or operator that writes or runs code is accepted. Ask for the data and work with it in the answer; a change to the data is proposed with connector_propose_write.";

    /// <summary>The journal could not be written.</summary>
    public const string JournalDown = "The answer is withheld: the query could not be written into the journal, and no data reaches an agent unrecorded.";

    /// <summary>What to do about it.</summary>
    public const string JournalDownHint = "Tell a person on the stand: the server's data folder is not writable.";

    /// <summary>An export file name that is not a plain name.</summary>
    public const string BadFileName = "The file name has to be a plain name of letters, digits, dots, dashes and underscores, without a folder.";

    /// <summary>A format the export does not write.</summary>
    public const string BadFormat = "The export writes csv or xlsx.";

    /// <summary>Neither a filter nor a pipeline was named, or both were.</summary>
    public const string OneQuery = "Name either a filter or a pipeline for the export, not both.";

    /// <summary>An answer cut by its limit or its size.</summary>
    public const string Cut = "There is more than this answer carries: narrow the filter, ask for fewer fields with a projection, or count first.";

    /// <summary>A failure that said nothing about itself.</summary>
    public const string Nothing = "Nothing came back.";

    /// <summary>An operation the proposal does not know.</summary>
    public const string BadOperation = "The operation is update, update_one, insert or delete.";

    /// <summary>An update or delete that names no document.</summary>
    public const string EmptyFilter = "An update or a delete needs a filter that names the documents: an empty one would touch every document of the collection.";

    /// <summary>How to narrow it.</summary>
    public const string EmptyFilterHint = "Name the documents by a field, such as {\"number\": \"2026-0042\"}; count first with connector_count.";

    /// <summary>How an update is written.</summary>
    public const string UpdateHint = "Write the update as field operators, such as {\"$set\": {\"status\": \"paid\"}}: $set, $unset, $inc, $mul, $min, $max, $rename, $currentDate, $push, $pull, $addToSet, $pop.";

    /// <summary>An insert that is not documents.</summary>
    public const string NotInsertable = "An insert takes a JSON array of document objects.";

    /// <summary>How to make a large write.</summary>
    public const string TooManyHint = "Narrow the filter, or split the work into several proposals a person can read.";

    /// <summary>The source has no writing user.</summary>
    public const string NoWriter = "This source has no user that may write: a person declared it read-only.";

    /// <summary>What to do about it.</summary>
    public const string NoWriterHint = "Say in your answer what you would change; a person adds a writing user to the source if writes are wanted.";

    /// <summary>The server has no channel for a person's decision.</summary>
    public const string NoApprovals = "Writes are off on this server: no approval key is set, so no person's decision could reach it.";

    /// <summary>How to turn them on.</summary>
    public const string NoApprovalsHint = "Set SNAIL_MCP_CONNECTOR_APPROVAL_KEY; on the stand, deploy/stand.sh up sets it.";

    /// <summary>What the agent does after proposing.</summary>
    public static string Proposed(string id) =>
        $"Nothing is written yet. Call request_write_approval with server \"connector\" and proposal \"{id}\": the task waits until the stand's owner or an administrator allows or refuses it. When the task goes on with the approval, call connector_apply with this proposal.";

    /// <summary>No proposal of that id.</summary>
    public static string NoProposal(string id) => $"No proposal is called \"{id}\".";

    /// <summary>A proposal of another task.</summary>
    public const string OtherTask = "This proposal was made by another task; only the task that proposed it may apply it.";

    /// <summary>A proposal nobody decided yet.</summary>
    public const string NotDecided = "Nobody allowed this proposal yet: nothing was written.";

    /// <summary>What to do meanwhile.</summary>
    public const string NotDecidedHint = "Call request_write_approval for it, and apply it once the task goes on with the approval.";

    /// <summary>A refused proposal.</summary>
    public static string Refused(string? comment) => $"A person refused this write{(string.IsNullOrWhiteSpace(comment) ? "." : $": {comment}")}";

    /// <summary>A proposal that already ran.</summary>
    public const string AlreadyApplied = "This proposal already ran; a write runs once.";

    /// <summary>A proposal another call is running, or one whose run was cut off before it ended.</summary>
    public const string BeingApplied = "This proposal is being applied by another call, or its run was cut off before it ended; it is not run twice.";

    /// <summary>A query whose answer the server could not read.</summary>
    public static string Unreadable(string what) => $"The query ran, but its answer could not be read ({what}); nothing of it is returned.";

    /// <summary>What to do about it.</summary>
    public const string UnreadableHint = "Cast the values the database answers with to text or a plain number in the query, or leave out the field that cannot be read.";

    /// <summary>What to do about it.</summary>
    public const string BeingAppliedHint = "Wait for the other call. If none is running, ask a person on the stand to look at the data before anything is proposed again.";

    /// <summary>A proposal past its days.</summary>
    public static string Expired(int days) => $"This proposal is older than {days} days and can no longer be applied: propose it again.";

    /// <summary>An update with operators a proposal does not use.</summary>
    public static string BadUpdate(string? operation) =>
        operation is null ? "The update changes nothing." : $"{operation} is not an update operator a proposal may use.";

    /// <summary>A write larger than one proposal may be.</summary>
    public static string TooMany(long count, int most) => $"The write would touch {count} documents or rows; one proposal touches at most {most}.";

    /// <summary>A source of that name is not declared.</summary>
    public static string NoSource(string name) => $"No source is called \"{name}\".";

    /// <summary>Where the names are.</summary>
    public const string NoSourceHint = "connector_sources lists the sources declared on the stand.";

    /// <summary>A collection the source does not allow.</summary>
    public static string CollectionKept(string source, string collection) => $"The source \"{source}\" does not allow the collection \"{collection}\".";

    /// <summary>Which collections it allows.</summary>
    public static string CollectionsHint(Source source) => $"It allows {string.Join(", ", source.Collections)}; connector_schema lists the ones that exist.";

    /// <summary>A part of a query that is not an object.</summary>
    public static string NotAnObject(string what) => $"The {what} is not a JSON object.";

    /// <summary>An operator this server does not run.</summary>
    public static string Forbidden(string operation) => $"{operation} is not run by this server: it writes or runs code inside the database.";

    /// <summary>A copy outside the workspaces root.</summary>
    public static string CopyOutside(string root, string workspaces) => $"The working copy \"{root}\" is not a task's copy under {workspaces}.";

    /// <summary>The database refused or failed the query.</summary>
    public static string DatabaseSaid(string source, string detail) => $"The database of \"{source}\" answered: {detail}";

    /// <summary>The query ran out of time.</summary>
    public static string TooSlow(int seconds) => $"The query took longer than {seconds} seconds and was stopped.";

    /// <summary>How to make it quicker.</summary>
    public const string TooSlowHint = "Narrow the filter to an indexed field (connector_schema lists the indexes), or count before reading.";

    /// <summary>Vault refused or failed.</summary>
    public static string VaultSaid(string detail) => $"The sources could not be read from Vault: {detail}";

    /// <summary>The sources a listing left out, each with why: a secret that lacks what it needs is named, not dropped silently.</summary>
    public static string SourcesUnread(IReadOnlyList<string> said) => $"Left out of the list: {string.Join(" ", said)}";

    /// <summary>A source whose secret lacks what it needs.</summary>
    public static string SourceIncomplete(string name, string key) => $"The source \"{name}\" is declared without {key}.";
}
