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
        else if (engine == "postgresql")
        {
            RequireNoForbiddenKeyword(check, PostgreSqlServerFunction());
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
    /// emptied, read the way <paramref name="engine"/> reads it; a quoted identifier becomes a spaced placeholder and a
    /// number is split from a word glued to it, so every keyword stands alone. <paramref name="statementEnds"/>, when given,
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

            // MySQL reads "--" as a comment only when an ASCII space or control character follows it ("1--1" is
            // arithmetic there, and so is "--" before a non-ASCII space, which MySQL reads as part of a name).
            var lineComment = c == '-' && next == '-'
                && (!mysql || i + 2 >= query.Length || query[i + 2] <= ' ' || query[i + 2] == '\x7F');
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
                // Quoted identifiers: a keyword inside one is a name, not a statement. PostgreSQL calls a quoted
                // function name like the bare one, so there "pg_sleep"(1) is checked by what the quotes hold.
                if (postgres && IsUnicodeEscapePrefix(query, i))
                {
                    throw new BusinessRuleException(
                        "The query may not use U&\"...\" identifiers; write the name without escapes.");
                }

                var close = c == '[' ? ']' : c;
                var start = i;
                i = SkipQuoted(query, i, close, backslashEscapes: false, open: c);
                if (postgres)
                {
                    RequireNoQuotedServerFunction(query[(start + 1)..(i - 1)].Replace("\"\"", "\"", StringComparison.Ordinal));
                }

                // Spaced, so a keyword written against the name ([a]DELETE[t]) is still a word of its own.
                output.Append(" x ");
                continue;
            }

            // A word glued to a number is a word of its own: SQL Server reads 1DELETE and $1COMMIT as a number then a
            // keyword. Where the number ends inside that word depends on how the engine reads an exponent or hex digits
            // (1eSET, 0xBEGIN), so each reading is put in the copy for the checks. A digit inside a name (t1delete)
            // does not start a number.
            if (char.IsAsciiDigit(c) && StartsNumber(query, i, sqlServer))
            {
                var end = i;
                while (end < query.Length && (char.IsAsciiDigit(query[end]) || query[end] == '.'))
                {
                    end++;
                }

                output.Append(query, i, end - i);
                var wordEnd = end;
                while (wordEnd < query.Length && IsIdentifierChar(query[wordEnd]))
                {
                    wordEnd++;
                }

                AppendGluedWordReadings(output, query, end, wordEnd);
                i = wordEnd;
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
    /// A column or table named like a keyword can be quoted: quoted identifiers are not checked against these lists
    /// (on PostgreSQL a quoted name of a blocked server function still is, see <see cref="RequireNoQuotedServerFunction"/>).</summary>
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

    /// <summary>Refuses a PostgreSQL quoted identifier that names one of the blocked server functions or dblink:
    /// <c>"pg_terminate_backend"(pid)</c> calls the function as the bare name does. Matched without regard to case,
    /// which also refuses a quoted name PostgreSQL would not resolve to the function; that costs nothing real.</summary>
    private static void RequireNoQuotedServerFunction(string name)
    {
        var function = PostgreSqlServerFunction().Match(name);
        var blocked = (function.Success && function.Index == 0 && function.Length == name.Length)
            || (name.StartsWith("dblink", StringComparison.OrdinalIgnoreCase) && name.All(ch => char.IsLetterOrDigit(ch) || ch == '_'));
        if (blocked)
        {
            throw new BusinessRuleException(
                $"The query may only read data; '{name.ToUpperInvariant()}' is not allowed, quoted or not.");
        }
    }

    /// <summary>PostgreSQL's U&amp;"..." identifier, whose \XXXX escapes could spell a blocked name the check would
    /// not see: a U (or u) and '&amp;' that stand alone as a token right before the quote.</summary>
    private static bool IsUnicodeEscapePrefix(string text, int quoteIndex) =>
        quoteIndex > 1
        && text[quoteIndex - 1] == '&'
        && text[quoteIndex - 2] is 'U' or 'u'
        && (quoteIndex == 2 || !IsIdentifierChar(text[quoteIndex - 3]));

    /// <summary>PostgreSQL's E'...' string: an E (or e) that stands alone as a token right before the quote, or that
    /// follows a number (PostgreSQL before 15 reads 1e'...' as the number 1, then an E'...' string).</summary>
    private static bool IsEscapeStringPrefix(string text, int quoteIndex)
    {
        if (quoteIndex == 0 || text[quoteIndex - 1] is not ('E' or 'e'))
        {
            return false;
        }

        var i = quoteIndex - 2;
        while (i >= 0 && (char.IsAsciiDigit(text[i]) || text[i] == '.'))
        {
            i--;
        }

        return i < 0 || !IsIdentifierChar(text[i]);
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '$';

    /// <summary>Whether the digit at <paramref name="i"/> starts a number rather than continuing a name. On SQL Server a
    /// '$' that does not continue a name starts a money literal ($1.50), so the digit after it starts a number too.</summary>
    private static bool StartsNumber(string text, int i, bool sqlServer)
    {
        if (i == 0 || !IsIdentifierChar(text[i - 1]))
        {
            return true;
        }

        return sqlServer && text[i - 1] == '$' && (i == 1 || !IsIdentifierChar(text[i - 2]));
    }

    /// <summary>Appends, each as a word of its own, the word <c>text[start..end)</c> glued to a number and what follows
    /// each place the number could end inside it: after a leading x or e (0xSET, 1eSET), and after the hex digits of
    /// 0x... or the digits of an exponent (0xBEGIN is 0xBE then GIN, 1e5DELETE is 1e5 then DELETE). Whichever way the
    /// engine reads the number, a keyword after it is seen. The copy is only checked, never run.</summary>
    private static void AppendGluedWordReadings(StringBuilder output, string text, int start, int end)
    {
        if (start == end)
        {
            return;
        }

        output.Append(' ').Append(text, start, end - start);
        if (text[start] is 'x' or 'X' or 'e' or 'E')
        {
            var hex = text[start] is 'x' or 'X';
            var after = start + 1;
            output.Append(' ').Append(text, after, end - after);
            while (after < end && (hex ? char.IsAsciiHexDigit(text[after]) : char.IsAsciiDigit(text[after])))
            {
                after++;
            }

            output.Append(' ').Append(text, after, end - after);
        }

        output.Append(' ');
    }

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

    // SQL Server statements that change the server or the session, or start a second statement, and need no ';' before
    // them (SELECT 1 BACKUP DATABASE ... and SELECT 1 DECLARE @x int SET @x = 1 ... are each one batch). All are reserved
    // words, so none can be an unquoted column name in a SELECT. USE is not here: it only changes which database the
    // rest, still checked, reads, which the login's own rights bound; and 'use' is a common column name
    // (ContactPoint.use). FETCH is not here either: OFFSET ... FETCH NEXT is part of a SELECT. Service Broker's RECEIVE
    // (which deletes a queue's messages) and SEND are not reserved, so a column of that name must be quoted; END only
    // as END CONVERSATION (and GET / MOVE CONVERSATION), since CASE ... END is part of a SELECT.
    [GeneratedRegex(@"\b(backup|restore|dbcc|kill|shutdown|waitfor|reconfigure|checkpoint|deny|revert|setuser|writetext|updatetext|declare|set|begin|if|while|goto|return|print|raiserror|throw|close|deallocate|commit|rollback|save|receive|send|(?:end|get|move)\s+conversation)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SqlServerStatement();

    // PostgreSQL functions a plain SELECT can call that read or write the server's files, change the session's settings
    // (set_config could undo the lexing TabularRowReader pins) or act on other sessions. Most need a superuser or a
    // pg_*_server_files role, which the login check refuses, but EXECUTE on them can also be granted one by one.
    [GeneratedRegex(@"\b(pg_read_file|pg_read_binary_file|pg_ls_\w+|pg_stat_file|pg_file_\w+|pg_logdir_ls|lo_import|lo_export|lo_get|lo_put|lo_from_bytea|lo_create|lo_creat|lo_unlink|lo_open|lo_truncate|lo_truncate64|loread|lowrite|set_config|pg_reload_conf|pg_rotate_logfile|pg_terminate_backend|pg_cancel_backend|pg_promote|pg_switch_wal|pg_create_\w+|pg_drop_replication_slot|pg_replication_origin_\w+|pg_sleep\w*|pg_advisory\w*)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PostgreSqlServerFunction();

    // PostgreSQL's tag rule: empty, or an identifier that does not start with a digit ($1 is a parameter).
    [GeneratedRegex(@"\$(?:[\p{L}_][\p{L}\p{Nd}_]*)?\$")]
    private static partial Regex DollarTag();

    [GeneratedRegex(@"^tabular-sql-(?<engine>sqlserver|postgresql|mysql)-[0-9a-f]{32}$")]
    private static partial Regex TabularSecretName();
}
