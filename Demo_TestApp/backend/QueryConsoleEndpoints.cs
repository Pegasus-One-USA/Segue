using System.Data.Common;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace HealthAppBackend;

// Backs the Admin "Query Console" screen: the user types a SQL query, the server runs it against the HealthApp
// database and returns the rows as a generic columns + rows grid.
//
// Safety is layered, and the layers are NOT equal:
//   1. Connection (the real boundary): the console uses its OWN connection string, ConnectionStrings:QueryConsole,
//      which must be a login with only db_datareader on the HealthApp database (see QueryConsole-ReadOnlyLogin.sql).
//      The endpoint refuses to run without it, so it can never fall back to the app's own (sa) login.
//   2. Admin-only (session cookie, same as the rest of the app).
//   3. Keyword screen: a guard rail so ordinary mistakes get a clear message instead of a permission error. The
//      scanner below blanks out comments, string literals and quoted/bracketed identifiers before screening, so
//      text hidden inside them is neither screened nor able to hide a keyword.
//   4. The query runs in a transaction that is always rolled back, with a row cap and a command timeout.
public static class QueryConsoleEndpoints
{
    private const string SessionCookieName = "hb_session";
    private const string ConnectionStringName = "QueryConsole";
    private const int MaxRows = 500;
    private const int CommandTimeoutSeconds = 15;

    // Anything that writes, changes schema/security, runs code, or controls the transaction. COMMIT / ROLLBACK /
    // BEGIN / TRAN / SAVE are here because the query runs inside a transaction that must stay open until WE roll it
    // back. SET / DECLARE are here because SQL Server allows a second statement after a SELECT with no separator.
    private static readonly Regex ForbiddenKeywords = new(
        @"\b(insert|update|delete|merge|drop|alter|create|truncate|exec|execute|grant|revoke|deny|backup|restore|shutdown|dbcc|into|xp_\w+|sp_\w+|openrowset|opendatasource|openquery|waitfor|use|commit|rollback|begin|transaction|tran|save|set|declare|kill|reconfigure|bulk|checkpoint)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private sealed record QueryRequest(string? Sql);

    public static void MapQueryConsoleEndpoints(this WebApplication app)
    {
        app.MapPost("/api/query-console/run", async (QueryRequest request, HttpContext http, SessionStore sessions, IConfiguration config, CancellationToken ct) =>
        {
            if (!http.Request.Cookies.TryGetValue(SessionCookieName, out var sessionId)
                || !sessions.TryGet(sessionId, out _, out _, out var role))
            {
                return Results.Unauthorized();
            }
            if (role != UserRoles.Admin)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var connectionString = config.GetConnectionString(ConnectionStringName);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return Results.Json(
                    new { error = $"The Query Console is not configured. Set ConnectionStrings:{ConnectionStringName} to a read-only login (see QueryConsole-ReadOnlyLogin.sql)." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var validationError = Validate(request.Sql, out var sql);
            if (validationError is not null)
            {
                return Results.BadRequest(new { error = validationError });
            }

            try
            {
                await using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync(ct);
                await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);
                try
                {
                    await using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = sql;
                    command.CommandTimeout = CommandTimeoutSeconds;

                    await using var reader = await command.ExecuteReaderAsync(ct);

                    var columns = new List<string>();
                    for (var i = 0; i < reader.FieldCount; i++)
                    {
                        var name = reader.GetName(i);
                        columns.Add(string.IsNullOrEmpty(name) ? $"Column{i + 1}" : name);
                    }

                    // Duplicate column names would collide as dictionary keys; suffix them once, up front, so the
                    // header list and every row agree on the keys.
                    var keys = new List<string>(columns.Count);
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    for (var i = 0; i < columns.Count; i++)
                    {
                        var key = seen.Add(columns[i]) ? columns[i] : $"{columns[i]}_{i + 1}";
                        seen.Add(key);
                        keys.Add(key);
                    }

                    var rows = new List<Dictionary<string, object?>>();
                    var truncated = false;
                    while (await reader.ReadAsync(ct))
                    {
                        if (rows.Count >= MaxRows)
                        {
                            truncated = true;
                            break;
                        }

                        var row = new Dictionary<string, object?>(keys.Count);
                        for (var i = 0; i < keys.Count; i++)
                        {
                            row[keys[i]] = ToJsonValue(reader.IsDBNull(i) ? null : reader.GetValue(i));
                        }
                        rows.Add(row);
                    }

                    return Results.Ok(new
                    {
                        columns = keys,
                        rows,
                        rowCount = rows.Count,
                        truncated,
                        maxRows = MaxRows,
                    });
                }
                finally
                {
                    // Always roll back - this endpoint must never persist anything. A rollback that itself fails
                    // (e.g. the transaction was already ended) must not replace the real result or error.
                    try
                    {
                        await transaction.RollbackAsync(CancellationToken.None);
                    }
                    catch (InvalidOperationException)
                    {
                    }
                    catch (DbException)
                    {
                    }
                }
            }
            catch (DbException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }

    // Returns an error message, or null when the statement is acceptable. 'sql' is the statement to execute: the
    // original text (comments and all, so SQL Server sees exactly what was screened), minus a trailing semicolon.
    private static string? Validate(string? input, out string sql)
    {
        sql = (input ?? string.Empty).Trim();
        if (sql.Length == 0)
        {
            return "Enter a query to run.";
        }

        var sanitized = Sanitize(sql);
        if (sanitized is null)
        {
            return "The query has an unterminated string, identifier or comment.";
        }

        var screened = sanitized.Trim().TrimEnd(';').Trim();
        if (!Regex.IsMatch(screened, @"^(select|with)\b", RegexOptions.IgnoreCase))
        {
            return "Only SELECT queries are allowed.";
        }
        if (screened.Contains(';'))
        {
            return "Only a single statement is allowed.";
        }

        var match = ForbiddenKeywords.Match(screened);
        if (match.Success)
        {
            return $"'{match.Value.ToUpperInvariant()}' is not allowed - this console is read-only.";
        }

        sql = sql.TrimEnd().TrimEnd(';');
        return null;
    }

    // One left-to-right pass that replaces everything which is not SQL "code" with a space (or an empty token):
    // -- line comments, /* block comments */ (T-SQL nests them), 'string literals', [bracketed identifiers] and
    // "quoted identifiers". Doing it in a single pass matters: stripping comments, then strings, then identifiers
    // as separate regexes lets one construct hide another (an apostrophe inside [brackets] looks like a string
    // start, and so on). Returns null when a construct is never closed.
    private static string? Sanitize(string sql)
    {
        var output = new StringBuilder(sql.Length);
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n' && sql[i] != '\r')
                {
                    i++;
                }
                output.Append(' ');
            }
            else if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var depth = 1;
                i += 2;
                while (i < sql.Length && depth > 0)
                {
                    if (sql[i] == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
                    {
                        depth++;
                        i += 2;
                    }
                    else if (sql[i] == '*' && i + 1 < sql.Length && sql[i + 1] == '/')
                    {
                        depth--;
                        i += 2;
                    }
                    else
                    {
                        i++;
                    }
                }
                if (depth > 0)
                {
                    return null;
                }
                output.Append(' ');
            }
            else if (c == '\'' || c == '"' || c == '[')
            {
                // Doubled closing delimiter ('' or "" or ]]) is an escaped literal character, not the end.
                var close = c == '[' ? ']' : c;
                i++;
                var closed = false;
                while (i < sql.Length)
                {
                    if (sql[i] == close)
                    {
                        if (i + 1 < sql.Length && sql[i + 1] == close)
                        {
                            i += 2;
                            continue;
                        }
                        i++;
                        closed = true;
                        break;
                    }
                    i++;
                }
                if (!closed)
                {
                    return null;
                }
                output.Append("''");
            }
            else
            {
                output.Append(c);
                i++;
            }
        }
        return output.ToString();
    }

    // System.Text.Json handles most CLR types, but raw bytes (rowversion, varbinary) would serialise as huge base64.
    private static object? ToJsonValue(object? value) => value switch
    {
        byte[] bytes => "0x" + Convert.ToHexString(bytes.Length > 32 ? bytes[..32] : bytes) + (bytes.Length > 32 ? "..." : string.Empty),
        _ => value,
    };
}
