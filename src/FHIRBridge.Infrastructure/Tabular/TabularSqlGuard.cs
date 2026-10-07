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
/// literal cannot be made to look closed to this check while the engine sees it open.</para>
/// </summary>
public static partial class TabularSqlGuard
{
    /// <summary>Validates <paramref name="query"/> for <paramref name="engine"/> and returns the text to execute: the
    /// query as written, trimmed.</summary>
    public static string ValidateQuery(string? query, string engine)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new BusinessRuleException("Enter the SELECT query that returns the rows.");
        }

        var check = Sanitise(query, engine).Trim().TrimEnd(';').Trim();
        if (!StartsWithSelect().IsMatch(check))
        {
            throw new BusinessRuleException("Only a SELECT query (optionally starting with WITH) can read rows.");
        }

        if (check.Contains(';'))
        {
            throw new BusinessRuleException("Enter a single SELECT statement, without ';' between statements.");
        }

        if (ForbiddenKeyword().Match(check) is { Success: true } forbidden)
        {
            throw new BusinessRuleException($"The query may only read data; '{forbidden.Value.ToUpperInvariant()}' is not allowed.");
        }

        return query.Trim();
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
    /// emptied, read the way <paramref name="engine"/> reads it.</summary>
    internal static string Sanitise(string query, string engine)
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
                i = SkipTo(query, i, "\n");
                output.Append(' ');
                continue;
            }

            if (c == '/' && next == '*')
            {
                var end = query.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    throw new BusinessRuleException("The query has a comment that is never closed.");
                }

                i = end + 2;
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

            if (postgres && c == '$' && DollarTag().Match(query, i) is { Success: true } tag && tag.Index == i)
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

            output.Append(c);
            i++;
        }

        return output.ToString();
    }

    /// <summary>PostgreSQL's E'...' string: an E (or e) that stands alone as a token right before the quote.</summary>
    private static bool IsEscapeStringPrefix(string text, int quoteIndex) =>
        quoteIndex > 0
        && text[quoteIndex - 1] is 'E' or 'e'
        && (quoteIndex == 1 || !(char.IsLetterOrDigit(text[quoteIndex - 2]) || text[quoteIndex - 2] is '_' or '$'));

    private static int SkipTo(string text, int start, string terminator)
    {
        var end = text.IndexOf(terminator, start, StringComparison.Ordinal);
        return end < 0 ? text.Length : end;
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

    [GeneratedRegex(@"\$[A-Za-z_]*\$")]
    private static partial Regex DollarTag();

    [GeneratedRegex(@"^tabular-sql-(?<engine>sqlserver|postgresql|mysql)-[0-9a-f]{32}$")]
    private static partial Regex TabularSecretName();
}
