using System.Text;
using System.Text.RegularExpressions;

namespace Snail.MCP.Connector.Application;

/// <summary>What a SQL statement may be: one SELECT, reading only tables the source allows.</summary>
/// <remarks>
/// Three walls, as with the document databases: the read-only user, a transaction that cannot write, and these rules,
/// which refuse with words a model can act on before the statement reaches the database. The rules read the statement
/// with its comments and string literals taken out, so a keyword hidden in either is neither a reason to refuse nor a
/// way in. Tables are read after FROM and JOIN; a name the source does not allow is refused, while what a subquery or a
/// common table expression names itself is left to the database user's own grants, which are the wall that counts.
/// </remarks>
public static partial class SqlRules
{
    /// <summary>The words no statement of this server may hold, as whole words.</summary>
    public static readonly IReadOnlyList<string> Forbidden =
    [
        "insert", "update", "delete", "merge", "upsert", "drop", "alter", "create", "truncate", "grant", "revoke", "exec",
        "execute", "call", "do", "copy", "into", "openrowset", "openquery", "opendatasource", "bulk", "shutdown", "dbcc",
        "backup", "restore", "vacuum", "lock", "set", "reindex", "cluster", "listen", "notify", "refresh", "attach", "detach"
    ];

    /// <summary>The statement as it is sent, or why it is not one this server runs against the source.</summary>
    public static Answer<string> Select(string? statement, Source source)
    {
        string text = statement?.Trim().TrimEnd(';').Trim() ?? string.Empty;
        string bare = Bare(text);

        if (bare.Length == 0)
            return Answer.Failed<string>(Messages.NotASelect, Messages.SelectHint);

        if (bare.Contains(';', StringComparison.Ordinal))
            return Answer.Failed<string>(Messages.OneStatement, Messages.SelectHint);

        IReadOnlyList<string> words = [.. Word().Matches(bare).Select(match => match.Value.ToLowerInvariant())];
        if (words.Count == 0 || words[0] is not ("select" or "with"))
            return Answer.Failed<string>(Messages.NotASelect, Messages.SelectHint);

        if (words.FirstOrDefault(word => Forbidden.Contains(word, StringComparer.Ordinal)) is { } forbidden)
            return Answer.Failed<string>(Messages.SqlForbidden(forbidden.ToUpperInvariant()), Messages.SelectHint);

        IReadOnlySet<string> named = Named(bare);
        string? kept = Tables(bare).FirstOrDefault(table => !named.Contains(table) && !IsAllowed(source, table));

        return kept is null ? Answer.Ok(text) : Answer.Failed<string>(Messages.CollectionKept(source.Name, kept), Messages.CollectionsHint(source));
    }

    /// <summary>The tables a statement reads after FROM and JOIN, each once, in the order written — as the journal names them.</summary>
    public static IReadOnlyList<string> Read(string statement)
    {
        string bare = Bare(statement);
        IReadOnlySet<string> named = Named(bare);

        return [.. Tables(bare).Where(table => !named.Contains(table)).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>Whether the source allows a table named bare or with its schema.</summary>
    public static bool IsAllowed(Source source, string table)
    {
        string bareName = table.Contains('.', StringComparison.Ordinal) ? table[(table.LastIndexOf('.') + 1)..] : table;

        return source.Allows(table) || source.Allows(bareName);
    }

    /// <summary>The statement with comments removed and every string literal emptied.</summary>
    private static string Bare(string text)
    {
        var bare = new StringBuilder(text.Length);
        int index = 0;

        while (index < text.Length)
        {
            if (text[index] == '-' && index + 1 < text.Length && text[index + 1] == '-')
            {
                index = text.IndexOf('\n', index) is var end && end < 0 ? text.Length : end;
                continue;
            }

            if (text[index] == '/' && index + 1 < text.Length && text[index + 1] == '*')
            {
                index = text.IndexOf("*/", index + 2, StringComparison.Ordinal) is var end && end < 0 ? text.Length : end + 2;
                bare.Append(' ');
                continue;
            }

            if (text[index] == '\'')
            {
                index = Closed(text, index);
                bare.Append("''");
                continue;
            }

            bare.Append(text[index]);
            index++;
        }

        return bare.ToString();
    }

    /// <summary>The index just past the string literal that opens at <paramref name="start"/>, a doubled quote kept inside it.</summary>
    private static int Closed(string text, int start)
    {
        int index = start + 1;

        while (index < text.Length)
        {
            if (text[index] == '\'' && index + 1 < text.Length && text[index + 1] == '\'')
            {
                index += 2;
                continue;
            }

            if (text[index] == '\'')
                return index + 1;

            index++;
        }

        return text.Length;
    }

    /// <summary>The names after FROM and JOIN, unquoted and lower-cased, with their schema when written.</summary>
    private static IEnumerable<string> Tables(string bare) =>
        Table().Matches(bare).Select(match => Unquoted(match.Groups["name"].Value));

    /// <summary>The names the statement gives its own results — common table expressions — which are not tables of the database.</summary>
    private static IReadOnlySet<string> Named(string bare) =>
        Expression().Matches(bare).Select(match => Unquoted(match.Groups["name"].Value)).ToHashSet(StringComparer.Ordinal);

    private static string Unquoted(string name) =>
        string.Join('.', name.Split('.').Select(part => part.Trim('"', '[', ']', '`').ToLowerInvariant()));

    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.CultureInvariant)]
    private static partial Regex Word();

    [GeneratedRegex(@"\b(?:from|join)\s+(?<name>(?:""[^""]+""|\[[^\]]+\]|[A-Za-z_][A-Za-z0-9_$]*)(?:\s*\.\s*(?:""[^""]+""|\[[^\]]+\]|[A-Za-z_][A-Za-z0-9_$]*))*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Table();

    [GeneratedRegex(@"(?:\bwith\b|,)\s*(?<name>""[^""]+""|\[[^\]]+\]|[A-Za-z_][A-Za-z0-9_]*)\s*(?:\([^)]*\)\s*)?\bas\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Expression();
}
