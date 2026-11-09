namespace Snail.MCP.Connector.Application;

/// <summary>The <c>type</c> of a refusal: the word a program branches on, where the error is prose for the model.</summary>
/// <remarks>
/// A contract like a proposal's state: the platform tells a write a person refused from one nobody decided yet, or a
/// query the server will never run from a database that failed it, by this word, never by the error's text, which stays
/// free to reword. The SDK's words where one fits, camel case like them where none does. A session's copy is typed by the
/// SDK's reason (<c>noRoot</c>, <c>outside</c>, <c>malformed</c>, <c>notATask</c>), a tool not granted is the SDK's
/// <c>notGranted</c>, a value the tool cannot work with — a filter, pipeline, statement, update, file name, format or wait
/// it does not take, or a tool asked of the wrong kind of source — is the SDK's <c>ToolFailures.Arguments</c>, and an
/// exports folder that is a link leading out of the copy is the SDK's <c>Reached.Outside</c>.
/// </remarks>
public static class FailureTypes
{
    /// <summary>What the server or the source never allows: an operator or statement that writes or runs code, a collection or table the source keeps out.</summary>
    public const string NotAllowed = "notAllowed";

    /// <summary>The server is not set up for it: no Vault address, or no approval key for writes.</summary>
    public const string NotConfigured = "notConfigured";

    /// <summary>No source is declared on the stand yet.</summary>
    public const string NoSources = "noSources";

    /// <summary>No source of the name asked for.</summary>
    public const string NoSuchSource = "noSuchSource";

    /// <summary>A source declared without something it needs: its kind, database or read address.</summary>
    public const string SourceIncomplete = "sourceIncomplete";

    /// <summary>Vault refused or failed the read of the sources.</summary>
    public const string VaultError = "vaultError";

    /// <summary>The database refused or failed the query or the write.</summary>
    public const string DatabaseError = "databaseError";

    /// <summary>The query ran past its time and was stopped.</summary>
    /// <remarks>The SDK's word for a program stopped at its time.</remarks>
    public const string Timeout = "timeout";

    /// <summary>The query ran, and its answer could not be read.</summary>
    public const string Unreadable = "unreadable";

    /// <summary>A write that would touch more documents or rows than one proposal may.</summary>
    public const string TooLarge = "tooLarge";

    /// <summary>The journal could not be written, so the answer or the write is withheld.</summary>
    public const string JournalDown = "journalDown";

    /// <summary>The source has no user that may write.</summary>
    public const string ReadOnly = "readOnly";

    /// <summary>No proposal of the id given.</summary>
    public const string NoSuchProposal = "noSuchProposal";

    /// <summary>A proposal another task made.</summary>
    public const string NotYours = "notYours";

    /// <summary>A proposal nobody decided yet.</summary>
    public const string Pending = "pending";

    /// <summary>A proposal a person refused.</summary>
    public const string Rejected = "rejected";

    /// <summary>A proposal that already ran.</summary>
    public const string Applied = "applied";

    /// <summary>A proposal another call is running, or one whose run was cut off before it ended.</summary>
    public const string Applying = "applying";

    /// <summary>A proposal past its days.</summary>
    public const string Expired = "expired";

    /// <summary>The data changed since the proposal: the write would touch more than the person allowed.</summary>
    public const string Changed = "changed";
}
