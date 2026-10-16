using System.Text;
using System.Text.RegularExpressions;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Services.Tabular;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.SharedKernel.Exceptions;

namespace FHIRBridge.Infrastructure.Tabular;

/// <summary>
/// The checks a Tabular SQL source must pass before its query runs: the query reads and does nothing else, and the
/// connection it runs over is one the Tabular form saved.
///
/// <para><b>Query.</b> One pass over the text recognises string literals, quoted identifiers and comments together
/// (a <c>--</c> inside <c>'%--%'</c> is text, not a comment), producing a copy with comments removed and literals
/// emptied. The checks run on that copy; the text the user wrote is what executes, so stripping can never change the
/// query that runs. MySQL's backslash escapes and PostgreSQL's dollar quotes are read as the engine reads them, so a
/// literal cannot be made to look closed to this check while the engine sees it open. That reading assumes the
/// server's default lexing: PostgreSQL with standard_conforming_strings on, MySQL without NO_BACKSLASH_ESCAPES or
/// ANSI_QUOTES. TabularRowReader sets those for the session before every query, so the assumption holds whatever the
/// server or the login is configured with.</para>
/// </summary>
public static partial class TabularSqlGuard
{
    /// <summary>Validates <paramref name="query"/> for <paramref name="engine"/> and returns the text to execute: the
    /// query as written, trimmed, without the ';' that closes it. A comment after that ';' stays, and callers wrap the
    /// text on its own lines, so the comment cannot swallow what follows.</summary>
    public static string ValidateQuery(string? query, string engine)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new BusinessRuleException("Enter the SELECT query that returns the rows.");
        }

        var terminators = new List<int>();
        var check = Sanitise(query, engine, terminators).Trim().TrimEnd(';').Trim();
        if (!StartsWithSelect().IsMatch(check))
        {
            throw new BusinessRuleException("Only a SELECT query (optionally starting with WITH) can read rows.");
        }

        if (check.Contains(';'))
        {
            throw new BusinessRuleException("Enter a single SELECT statement, without ';' between statements.");
        }

        RequireNoForbiddenKeyword(check, ForbiddenKeyword());
        if (engine == "sqlserver")
        {
            // SQL Server runs several statements in one batch with no ';' between them.
            RequireNoForbiddenKeyword(check, SqlServerStatement());
        }

        // Every ';' outside literals and comments is now a closing one (the check above refused any other).
        var executable = new StringBuilder(query);
        for (var t = terminators.Count - 1; t >= 0; t--)
        {
            executable.Remove(terminators[t], 1);
        }

        return executable.ToString().Trim();
    }

    /// <summary>
    /// The connection secret must be one the Tabular form's "Save connection" wrote: in the Tabular vault, named
    /// <c>tabular-sql-{engine}-{id}</c> for the same engine. A reference typed into a request or a node's settings can
    /// therefore never reach another connection's secret (a destination's or an EHR's credentials).
    /// </summary>
    public static void RequireTabularConnectionSecret(SecretReference secret, string engine, ITenantSecretVaultResolver vaults)
    {
        var expectedVault = vaults.ResolveVaultName(TabularSourceService.SqlConnectionVaultName);
        var name = TabularSecretName().Match(secret.SecretName ?? string.Empty);
        if (!string.Equals(secret.KeyVaultName, expectedVault, StringComparison.OrdinalIgnoreCase)
            || !name.Success
            || !string.Equals(name.Groups["engine"].Value, engine, StringComparison.Ordinal))
        {
            throw new BusinessRuleException(
                "This source's database connection was not saved from the Tabular source form. Save the connection again.");
        }
    }

    /// <summary>The query with comments removed (a space each) and every string literal and quoted identifier
    /// emptied, read the way <paramref name="engine"/> reads it. <paramref name="statementEnds"/>, when given,
    /// receives the index in <paramref name="query"/> of every ';' that is code.</summary>
    internal static string Sanitise(string query, string engine, List<int>? statementEnds = null)
    {
        var mysql = engine == "mysql";
        var postgres = engine == "postgresql";
        var sqlServer = engine == "sqlserver";
        var output = new StringBuilder(query.Length);
        var i = 0;
        while (i < query.Length)
        {
            var c = query[i];
            var next = i + 1 < query.Length ? query[i + 1] : '\0';

            // MySQL reads "--" as a comment only when whitespace follows it ("1--1" is arithmetic there).
            var lineComment = c == '-' && next == '-'
                && (!mysql || i + 2 >= query.Length || char.IsWhiteSpace(query[i + 2]));
            if (lineComment || (mysql && c == '#'))
            {
                i = SkipToLineEnd(query, i);
                output.Append(' ');
                continue;
            }

            if (c == '/' && next == '*')
            {
                // MySQL runs the text of /*! ... */ (and MariaDB of /*M! ... */) as code, so it is not a comment
                // this check may strip.
                if (mysql && IsExecutableComment(query, i))
                {
                    throw new BusinessRuleException("The query may not contain /*! ... */ comments; MySQL runs their text.");
                }

                i = SkipBlockComment(query, i, nested: postgres || sqlServer);
                output.Append(' ');
                continue;
            }

            if (c == '\'' || (mysql && c == '"'))
            {
                i = SkipQuoted(query, i, c, backslashEscapes: mysql || (postgres && IsEscapeStringPrefix(query, i)));
                output.Append("''");
                continue;
            }

            if (c == '"' || (mysql && c == '`') || (sqlServer && c == '['))
            {
                // Quoted identifiers: a keyword inside one is a name, not a statement.
                var close = c == '[' ? ']' : c;
                i = SkipQuoted(query, i, close, backslashEscapes: false, open: c);
                output.Append("x");
                continue;
            }

            // A '$' straight after an identifier character is part of that identifier (a$x$), not a dollar quote.
            if (postgres && c == '$' && !(i > 0 && IsIdentifierChar(query[i - 1]))
                && DollarTag().Match(query, i) is { Success: true } tag && tag.Index == i)
            {
                var end = query.IndexOf(tag.Value, i + tag.Length, StringComparison.Ordinal);
                if (end < 0)
                {
                    throw new BusinessRuleException("The query has a quoted string that is never closed.");
                }

                i = end + tag.Length;
                output.Append("''");
                continue;
            }

            if (c == ';')
            {
                statementEnds?.Add(i);
            }

            output.Append(c);
            i++;
        }

        return output.ToString();
    }

    /// <summary>Refuses the first match of <paramref name="keywords"/> in the sanitised query, except a call to a name
    /// that is also a read-only function (<see cref="ReadOnlyFunctions"/>: MySQL's TRUNCATE(x, d) and INSERT(str, ...)).
    /// A column or table named like a keyword can be quoted: quoted identifiers are not checked.</summary>
    private static void RequireNoForbiddenKeyword(string check, Regex keywords)
    {
        foreach (Match match in keywords.Matches(check))
        {
            if (ReadOnlyFunctions.Contains(match.Value) && IsCall(check, match.Index + match.Length))
            {
                continue;
            }

            throw new BusinessRuleException(
                $"The query may only read data; '{match.Value.ToUpperInvariant()}' is not allowed. " +
                "If it is the name of a column or table, put it in quotes.");
        }
    }

    // Never the statement forms: TRUNCATE TABLE and INSERT INTO / INSERT t are not followed by '('.
    private static readonly HashSet<string> ReadOnlyFunctions = new(StringComparer.OrdinalIgnoreCase) { "truncate", "insert" };

    private static bool IsCall(string text, int afterName)
    {
        var i = afterName;
        while (i < text.Length && char.IsWhiteSpace(text[i]))
        {
            i++;
        }

        return i < text.Length && text[i] == '(';
    }

    /// <summary>PostgreSQL's E'...' string: an E (or e) that stands alone as a token right before the quote.</summary>
    private static bool IsEscapeStringPrefix(string text, int quoteIndex) =>
        quoteIndex > 0
        && text[quoteIndex - 1] is 'E' or 'e'
        && (quoteIndex == 1 || !IsIdentifierChar(text[quoteIndex - 2]));

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '$';

    /// <summary>MySQL's /*! ... */ and MariaDB's /*M! ... */, whose text the server executes.</summary>
    private static bool IsExecutableComment(string text, int start) =>
        (start + 2 < text.Length && text[start + 2] == '!')
        || (start + 3 < text.Length && text[start + 2] is 'M' or 'm' && text[start + 3] == '!');

    /// <summary>Index just past the block comment that opens at <paramref name="start"/>. PostgreSQL nests block
    /// comments, so with <paramref name="nested"/> each inner /* needs its own */.</summary>
    private static int SkipBlockComment(string text, int start, bool nested)
    {
        var depth = 1;
        var i = start + 2;
        while (i + 1 < text.Length)
        {
            if (text[i] == '*' && text[i + 1] == '/')
            {
                i += 2;
                if (--depth == 0)
                {
                    return i;
                }

                continue;
            }

            if (nested && text[i] == '/' && text[i + 1] == '*')
            {
                depth++;
                i += 2;
                continue;
            }

            i++;
        }

        throw new BusinessRuleException("The query has a comment that is never closed.");
    }

    /// <summary>Index of the '\n' that ends a line comment. Engines disagree on a lone '\r' (PostgreSQL and SQL Server
    /// end the comment there, MySQL does not), and either reading could hide code from this check under the other, so
    /// a line comment holding one is refused. A '\r\n' line break is fine.</summary>
    private static int SkipToLineEnd(string text, int start)
    {
        var end = text.IndexOf('\n', start);
        end = end < 0 ? text.Length : end;
        for (var i = start; i < end; i++)
        {
            if (text[i] == '\r' && i + 1 != end)
            {
                throw new BusinessRuleException("A comment in the query contains a carriage return without a line break.");
            }
        }

        return end;
    }

    /// <summary>Index just past the quoted run that opens at <paramref name="start"/>. A doubled closing quote is an
    /// escaped quote; with <paramref name="backslashEscapes"/>, so is a backslash before any character.</summary>
    private static int SkipQuoted(string text, int start, char close, bool backslashEscapes, char? open = null)
    {
        var i = start + 1;
        while (i < text.Length)
        {
            var c = text[i];
            if (backslashEscapes && c == '\\')
            {
                i += 2;
                continue;
            }

            if (c == close)
            {
                if (open != '[' && i + 1 < text.Length && text[i + 1] == close)
                {
                    i += 2;
                    continue;
                }

                if (open == '[' && i + 1 < text.Length && text[i + 1] == ']')
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            i++;
        }

        throw new BusinessRuleException("The query has a quoted string that is never closed.");
    }

    [GeneratedRegex(@"^\(*\s*(select|with)\b", RegexOptions.IgnoreCase)]
    private static partial Regex StartsWithSelect();

    // Statement keywords, plus the functions that run text on another server (whose literal the keyword check cannot
    // see into): OPENQUERY / OPENROWSET / OPENDATASOURCE on SQL Server, dblink on PostgreSQL. REPLACE(), SET and
    // COMMENT also appear in ordinary reads, and the read-only transaction / read-only login is the backstop for
    // anything a keyword list misses.
    [GeneratedRegex(@"\b(insert|update|delete|merge|drop|alter|create|truncate|exec|execute|grant|revoke|into|call|copy|vacuum|attach|pragma|rename|openquery|openrowset|opendatasource|dblink\w*)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ForbiddenKeyword();

    // SQL Server statements that change the server or the session and need no ';' before them (SELECT 1 BACKUP DATABASE
    // ... is one batch). USE is not here: it only changes which database the rest, still checked, reads, which the
    // login's own rights bound; and 'use' is a common column name (ContactPoint.use).
    [GeneratedRegex(@"\b(backup|restore|dbcc|kill|shutdown|waitfor|reconfigure|checkpoint|deny|revert|setuser|writetext|updatetext)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SqlServerStatement();

    // PostgreSQL's tag rule: empty, or an identifier that does not start with a digit ($1 is a parameter).
    [GeneratedRegex(@"\$(?:[\p{L}_][\p{L}\p{Nd}_]*)?\$")]
    private static partial Regex DollarTag();

    [GeneratedRegex(@"^tabular-sql-(?<engine>sqlserver|postgresql|mysql)-[0-9a-f]{32}$")]
    private static partial Regex TabularSecretName();
}
