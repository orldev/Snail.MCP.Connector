namespace Snail.MCP.Connector.Domain;

/// <summary>Which engine a company database runs on.</summary>
public enum SourceKind
{
    /// <summary>MongoDB: collections of documents, read with filters and aggregation pipelines.</summary>
    MongoDb,

    /// <summary>Microsoft SQL Server: tables, read with SELECT.</summary>
    SqlServer,

    /// <summary>PostgreSQL: tables, read with SELECT.</summary>
    PostgreSql
}

/// <summary>A company database the agents may read, as a person declared it; the credentials stay apart in <see cref="SourceAccess"/>.</summary>
/// <param name="Name">The name tools are called with, unique on the stand.</param>
/// <param name="Kind">The engine.</param>
/// <param name="Database">The database read inside the server.</param>
/// <param name="Collections">The collections or tables an agent may read; <c>*</c> alone allows every one.</param>
public sealed record Source(string Name, SourceKind Kind, string Database, IReadOnlyList<string> Collections)
{
    /// <summary>What a person wrote about the data, so an agent knows what it holds.</summary>
    public string? Description { get; init; }

    /// <summary>Whether a person declared a user that may write: without one, no proposal to write is ever made.</summary>
    public bool CanWrite { get; init; }

    /// <summary>The wildcard that allows every collection.</summary>
    public const string Every = "*";

    /// <summary>Whether an agent may read the collection.</summary>
    public bool Allows(string collection) =>
        Collections.Contains(Every, StringComparer.Ordinal) || Collections.Contains(collection, StringComparer.Ordinal);
}

/// <summary>The connection strings of a source: the read-only user always, the writing user only when one was declared.</summary>
/// <remarks>
/// Two users and not one flag: a read-only user is a promise the database keeps itself, whatever this server gets wrong.
/// The owner's decision of 15.10.2026 — every read goes through the user that cannot write.
/// </remarks>
/// <param name="ReadUri">The connection string of the user that may only read.</param>
/// <param name="WriteUri">The connection string of the user that may write; null when there is none.</param>
public sealed record SourceAccess(string ReadUri, string? WriteUri);

/// <summary>The sources a listing could read, and why it left out the ones it could not.</summary>
/// <remarks>
/// A secret a person filled in half is named rather than missing without a word. What was left out is part of what was
/// listed, not the hint of a failure: <c>Answer.Hint</c> says what to do about a failure, and a success's hint never
/// reaches the envelope, so connector_diagnose dropped it.
/// </remarks>
/// <param name="Sources">The sources read, without their credentials.</param>
/// <param name="Unread">What each source left out said about itself; empty when every one was read.</param>
public sealed record DeclaredSources(IReadOnlyList<Source> Sources, IReadOnlyList<string> Unread);

/// <summary>A source together with how to reach it.</summary>
/// <param name="Source">What the source is.</param>
/// <param name="Access">How to reach it.</param>
public sealed record ReachableSource(Source Source, SourceAccess Access);

/// <summary>The collections of a source an agent may read.</summary>
/// <param name="Source">The source.</param>
/// <param name="Collections">The ones that exist and the source allows, in name order.</param>
public sealed record SourceCollections(Source Source, IReadOnlyList<string> Collections);
