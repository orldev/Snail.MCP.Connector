using System.Text;
using System.Text.RegularExpressions;
using Snail.Sdk.Mcp.Hosting;

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
    /// <remarks>
    /// Besides the words that write or run code: the ones that end or open a transaction, which would take a statement out
    /// of the read-only one it runs in, and PostgreSQL's functions that read a table, a query or a file named in a string,
    /// where no FROM shows it. A list cannot name every such function; the read-only user's grants are the wall.
    /// </remarks>
    public static readonly IReadOnlyList<string> Forbidden =
    [
        "insert", "update", "delete", "merge", "upsert", "drop", "alter", "create", "truncate", "grant", "revoke", "exec",
        "execute", "call", "do", "copy", "into", "openrowset", "openquery", "opendatasource", "bulk", "shutdown", "dbcc",
        "backup", "restore", "vacuum", "lock", "set", "reindex", "cluster", "listen", "notify", "refresh", "attach", "detach",
        "commit", "rollback", "savepoint", "begin",
        "query_to_xml", "query_to_xmlschema", "query_to_xml_and_xmlschema", "table_to_xml", "table_to_xmlschema",
        "table_to_xml_and_xmlschema", "cursor_to_xml", "cursor_to_xmlschema", "schema_to_xml", "schema_to_xmlschema",
        "schema_to_xml_and_xmlschema", "database_to_xml", "database_to_xmlschema", "database_to_xml_and_xmlschema", "ts_stat",
        "dblink", "dblink_exec", "pg_read_file", "pg_read_binary_file", "pg_ls_dir", "pg_stat_file", "lo_import", "lo_export",
        "lo_get"
    ];

    /// <summary>The statement as it is sent, or why it is not one this server runs against the source.</summary>
    public static Answer<string> Select(string? statement, Source source)
    {
        string text = statement?.Trim().TrimEnd(';').Trim() ?? string.Empty;
        string bare = Bare(text, source.Kind);

        if (bare.Length == 0)
            return Answer.Failed(Messages.NotASelect, Messages.SelectHint, ToolFailures.Arguments);

        if (bare.Contains(';', StringComparison.Ordinal))
            return Answer.Failed(Messages.OneStatement, Messages.SelectHint, ToolFailures.Arguments);

        IReadOnlyList<string> words = [.. Word().Matches(bare).Select(match => match.Value.ToLowerInvariant())];
        if (words.Count == 0 || words[0] is not ("select" or "with"))
            return Answer.Failed(Messages.NotASelect, Messages.SelectHint, ToolFailures.Arguments);

        if (words.FirstOrDefault(word => Forbidden.Contains(word, StringComparer.Ordinal)) is { } forbidden)
            return Answer.Failed(Messages.SqlForbidden(forbidden.ToUpperInvariant()), Messages.SelectHint, FailureTypes.NotAllowed);

        IReadOnlySet<string> named = Named(bare);
        string? kept = Tables(bare).FirstOrDefault(table => !named.Contains(table) && !IsAllowed(source, table));

        return kept is null ? Answer.Ok(text) : Answer.Failed(Messages.CollectionKept(source.Name, kept), Messages.CollectionsHint(source), FailureTypes.NotAllowed);
    }

    /// <summary>The tables a statement reads after FROM and JOIN, each once, in the order written — as the journal names them.</summary>
    public static IReadOnlyList<string> Read(string statement, SourceKind kind)
    {
        string bare = Bare(statement, kind);
        IReadOnlySet<string> named = Named(bare);

        return [.. Tables(bare).Where(table => !named.Contains(table)).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>Whether the source allows a table named bare or with its schema.</summary>
    /// <remarks>
    /// A SQL database names its tables in any case — SQL Server keeps <c>Invoices</c> as written, a statement may say
    /// <c>invoices</c> — so a SQL source compares names without case; a document database's collections keep theirs.
    /// </remarks>
    public static bool IsAllowed(Source source, string table)
    {
        string bareName = table.Contains('.', StringComparison.Ordinal) ? table[(table.LastIndexOf('.') + 1)..] : table;
        if (source.Kind is SourceKind.MongoDb)
            return source.Allows(table) || source.Allows(bareName);

        return source.Collections.Any(allowed => allowed == Source.Every
            || string.Equals(allowed, table, StringComparison.OrdinalIgnoreCase)
            || string.Equals(allowed, bareName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The statement with comments removed and every string literal emptied, read in the engine's own quoting.</summary>
    /// <remarks>
    /// A quote counts only where the engine reads one: inside a quoted name — <c>"a'"</c>, and <c>[a']</c> on SQL Server —
    /// it is a letter of the name; in PostgreSQL's <c>E'\''</c> a backslash escapes it; <c>$$…$$</c> is a literal too.
    /// Read any other way, a quote the database never sees opens a literal that hides the rest of the statement — its
    /// FROM, a second statement — from every rule here (audit of 30.10.2026). A bracket is a name only on SQL Server: in
    /// PostgreSQL it is an array's or a jsonb's subscript, <c>data[']']</c>, and read as a name it would hide the same way.
    /// </remarks>
    private static string Bare(string text, SourceKind kind)
    {
        var bare = new StringBuilder(text.Length);
        int index = 0;

        while (index < text.Length)
        {
            char current = text[index];
            char next = index + 1 < text.Length ? text[index + 1] : '\0';

            if (current == '-' && next == '-')
            {
                index = text.IndexOf('\n', index) is var end && end < 0 ? text.Length : end;
                continue;
            }

            if (current == '/' && next == '*')
            {
                index = text.IndexOf("*/", index + 2, StringComparison.Ordinal) is var end && end < 0 ? text.Length : end + 2;
                bare.Append(' ');
                continue;
            }

            if (current == '"' || (current == '[' && kind is SourceKind.SqlServer))
            {
                int closed = NameEnd(text, index, current == '"' ? '"' : ']');
                bare.Append(text, index, closed - index);
                index = closed;
                continue;
            }

            if (current == '\'')
            {
                index = Closed(text, index, isEscaping: kind is SourceKind.PostgreSql && IsEscapeString(text, index));
                bare.Append("''");
                continue;
            }

            if (current == '$' && kind is SourceKind.PostgreSql && DollarTag(text, index) is { } tag)
            {
                index = text.IndexOf(tag, index + tag.Length, StringComparison.Ordinal) is var end && end < 0 ? text.Length : end + tag.Length;
                bare.Append("''");
                continue;
            }

            bare.Append(current);
            index++;
        }

        return bare.ToString();
    }

    /// <summary>The index just past the quoted name that opens at <paramref name="start"/>, a doubled closing mark kept inside it.</summary>
    private static int NameEnd(string text, int start, char closing)
    {
        int index = start + 1;

        while (index < text.Length)
        {
            if (text[index] == closing && index + 1 < text.Length && text[index + 1] == closing)
            {
                index += 2;
                continue;
            }

            if (text[index] == closing)
                return index + 1;

            index++;
        }

        return text.Length;
    }

    /// <summary>Whether the literal at <paramref name="quote"/> is PostgreSQL's escape string: <c>E'…'</c>, the E standing alone.</summary>
    private static bool IsEscapeString(string text, int quote) =>
        quote > 0 && text[quote - 1] is 'E' or 'e' && (quote == 1 || !IsNamePart(text[quote - 2]));

    /// <summary>The <c>$tag$</c> that opens a dollar-quoted literal at <paramref name="start"/>; null when none opens there.</summary>
    private static string? DollarTag(string text, int start)
    {
        if (start > 0 && IsNamePart(text[start - 1]))
            return null;

        int index = start + 1;
        while (index < text.Length && text[index] != '$' && IsNamePart(text[index]))
            index++;

        bool isTag = index < text.Length && text[index] == '$' && (index == start + 1 || !char.IsAsciiDigit(text[start + 1]));

        return isTag ? text[start..(index + 1)] : null;
    }

    private static bool IsNamePart(char letter) => char.IsLetterOrDigit(letter) || letter is '_' or '$';

    /// <summary>The index just past the string literal that opens at <paramref name="start"/>, a doubled quote kept inside it.</summary>
    private static int Closed(string text, int start, bool isEscaping)
    {
        int index = start + 1;

        while (index < text.Length)
        {
            if (isEscaping && text[index] == '\\')
            {
                index += 2;
                continue;
            }

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

    /// <summary>The words that end a table's alias: a clause, a join or a set operation.</summary>
    private static readonly IReadOnlySet<string> Clauses = new HashSet<string>(StringComparer.Ordinal)
    {
        "where", "group", "order", "having", "limit", "offset", "join", "inner", "left", "right", "full", "outer", "cross",
        "natural", "on", "using", "union", "except", "intersect", "window", "fetch", "for", "tablesample", "with", "pivot",
        "unpivot", "apply", "lateral", "option", "select", "into", "values", "returning"
    };

    /// <summary>The functions whose own argument list says FROM without naming a table.</summary>
    private static readonly IReadOnlySet<string> Spelled = new HashSet<string>(StringComparer.Ordinal) { "extract", "substring", "trim", "overlay", "position" };

    /// <summary>Every table the statement reads: each one after FROM, JOIN or APPLY, and each one of a comma-separated FROM list.</summary>
    /// <remarks>
    /// A FROM list names its tables one after another — <c>FROM invoices i, secrets s</c> — so every item is read, not the
    /// first: a table kept out must not come in second. A subquery is skipped here because its own FROM is read in turn; a
    /// function after FROM is named like a table, so one the source does not allow is refused too. FROM inside
    /// <c>EXTRACT(year FROM issued)</c> and its kind names no table.
    /// </remarks>
    private static IEnumerable<string> Tables(string bare)
    {
        IReadOnlyList<string> tokens = [.. Token().Matches(bare).Select(match => match.Value)];
        var opened = new Stack<string>();

        for (int index = 0; index < tokens.Count; index++)
        {
            string word = tokens[index].ToLowerInvariant();
            if (word == "(")
            {
                opened.Push(index > 0 ? tokens[index - 1].ToLowerInvariant() : string.Empty);
                continue;
            }

            if (word == ")")
            {
                opened.TryPop(out _);
                continue;
            }

            if (word is not ("from" or "join" or "apply") || (word == "from" && opened.TryPeek(out string? owner) && Spelled.Contains(owner)))
                continue;

            int at = index + 1;
            while (at < tokens.Count)
            {
                var item = Item(tokens, at);
                if (item.Name is { } name)
                    yield return name;

                at = item.Next;
                if (at >= tokens.Count || tokens[at] != ",")
                    break;

                at++;
            }
        }
    }

    /// <summary>One item of a FROM list read from <paramref name="at"/>: the table it names, if any, and where the item ends.</summary>
    private static TableItem Item(IReadOnlyList<string> tokens, int at)
    {
        while (at < tokens.Count && tokens[at].ToLowerInvariant() is "lateral" or "only")
            at++;

        if (at >= tokens.Count)
            return new TableItem(null, at);

        string? name = null;
        if (tokens[at] == "(")
        {
            at = AfterParentheses(tokens, at);
        }
        else if (IsName(tokens[at]))
        {
            var parts = new List<string> { tokens[at] };
            at++;
            while (at + 1 < tokens.Count && tokens[at] == "." && IsName(tokens[at + 1]))
            {
                parts.Add(tokens[at + 1]);
                at += 2;
            }

            name = Unquoted(string.Join('.', parts));
            if (at < tokens.Count && tokens[at] == "(")
                at = AfterParentheses(tokens, at);
        }

        if (at < tokens.Count && tokens[at].Equals("as", StringComparison.OrdinalIgnoreCase))
            at += 2;
        else if (at < tokens.Count && IsName(tokens[at]) && !Clauses.Contains(tokens[at].ToLowerInvariant()))
            at++;

        return new TableItem(name, at);
    }

    private static int AfterParentheses(IReadOnlyList<string> tokens, int at)
    {
        int depth = 0;
        for (; at < tokens.Count; at++)
        {
            depth += tokens[at] == "(" ? 1 : tokens[at] == ")" ? -1 : 0;
            if (depth == 0)
                return at + 1;
        }

        return at;
    }

    private static bool IsName(string token) => token.Length > 0 && (char.IsLetter(token[0]) || token[0] is '_' or '"' or '[' or '`');

    /// <summary>A table a FROM list names, or none for a subquery, and the index the item ends at.</summary>
    private sealed record TableItem(string? Name, int Next);

    /// <summary>The names the statement gives its own results — common table expressions — which are not tables of the database.</summary>
    private static IReadOnlySet<string> Named(string bare) =>
        Expression().Matches(bare).Select(match => Unquoted(match.Groups["name"].Value)).ToHashSet(StringComparer.Ordinal);

    private static string Unquoted(string name) =>
        string.Join('.', name.Split('.').Select(part => part.Trim('"', '[', ']', '`').ToLowerInvariant()));

    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.CultureInvariant)]
    private static partial Regex Word();

    [GeneratedRegex(@"""[^""]+""|\[[^\]]+\]|`[^`]+`|[A-Za-z_][A-Za-z0-9_$]*|\S", RegexOptions.CultureInvariant)]
    private static partial Regex Token();

    [GeneratedRegex(@"(?:\bwith\b|,)\s*(?<name>""[^""]+""|\[[^\]]+\]|[A-Za-z_][A-Za-z0-9_]*)\s*(?:\([^)]*\)\s*)?\bas\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Expression();
}
