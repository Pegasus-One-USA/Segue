using System.Data;
using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace HealthAppBackend;

// Backs the Admin "Query Console" screen: the user types a SQL query, the server runs it against the HealthApp
// database and returns the rows as a generic columns + rows grid. Admin-only (session cookie, same as the rest of
// the app) and deliberately read-only: a single SELECT/WITH statement, no write/DDL/exec keywords, run inside a
// transaction that is always rolled back, with a row cap and a command timeout. The keyword screen is a guard
// rail for a demo, not a security boundary on its own - the rollback and Admin gate are what make it safe.
public static class QueryConsoleEndpoints
{
    private const string SessionCookieName = "hb_session";
    private const int MaxRows = 500;
    private const int CommandTimeoutSeconds = 15;

    private static readonly Regex ForbiddenKeywords = new(
        @"\b(insert|update|delete|merge|drop|alter|create|truncate|exec|execute|grant|revoke|deny|backup|restore|shutdown|dbcc|into|xp_\w+|sp_\w+|openrowset|opendatasource|openquery|waitfor|use)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Comments = new(@"--[^\r\n]*|/\*.*?\*/", RegexOptions.Singleline | RegexOptions.Compiled);

    private sealed record QueryRequest(string? Sql);

    public static void MapQueryConsoleEndpoints(this WebApplication app)
    {
        app.MapPost("/api/query-console/run", async (QueryRequest request, HttpContext http, SessionStore sessions, HealthAppDbContext db, CancellationToken ct) =>
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

            var validationError = Validate(request.Sql, out var sql);
            if (validationError is not null)
            {
                return Results.BadRequest(new { error = validationError });
            }

            var connection = db.Database.GetDbConnection();
            var openedHere = connection.State != ConnectionState.Open;
            if (openedHere)
            {
                await connection.OpenAsync(ct);
            }

            try
            {
                await using var transaction = await connection.BeginTransactionAsync(ct);
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

                    var rows = new List<Dictionary<string, object?>>();
                    var truncated = false;
                    while (await reader.ReadAsync(ct))
                    {
                        if (rows.Count >= MaxRows)
                        {
                            truncated = true;
                            break;
                        }

                        var row = new Dictionary<string, object?>(columns.Count);
                        for (var i = 0; i < columns.Count; i++)
                        {
                            // Duplicate column names would collide as dictionary keys; suffix to keep every value.
                            var key = row.ContainsKey(columns[i]) ? $"{columns[i]}_{i + 1}" : columns[i];
                            row[key] = ToJsonValue(reader.IsDBNull(i) ? null : reader.GetValue(i));
                        }
                        rows.Add(row);
                    }

                    return Results.Ok(new
                    {
                        columns = rows.Count > 0 ? rows[0].Keys.ToList() : columns,
                        rows,
                        rowCount = rows.Count,
                        truncated,
                        maxRows = MaxRows,
                    });
                }
                finally
                {
                    // Always roll back - this endpoint must never persist anything.
                    await transaction.RollbackAsync(CancellationToken.None);
                }
            }
            catch (DbException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            finally
            {
                if (openedHere)
                {
                    await connection.CloseAsync();
                }
            }
        });
    }

    // Returns an error message, or null when the statement is acceptable. 'sql' is the trimmed statement with any
    // trailing semicolon removed.
    private static string? Validate(string? input, out string sql)
    {
        sql = (input ?? string.Empty).Trim();
        if (sql.Length == 0)
        {
            return "Enter a query to run.";
        }

        var stripped = Comments.Replace(sql, " ").Trim().TrimEnd(';').Trim();
        if (!Regex.IsMatch(stripped, @"^(select|with)\b", RegexOptions.IgnoreCase))
        {
            return "Only SELECT queries are allowed.";
        }
        if (stripped.Contains(';'))
        {
            return "Only a single statement is allowed.";
        }

        // String literals can legitimately contain words like 'update' or 'use' - drop them before the keyword screen.
        var withoutLiterals = Regex.Replace(stripped, @"'(?:[^']|'')*'", "''");
        var match = ForbiddenKeywords.Match(withoutLiterals);
        if (match.Success)
        {
            return $"'{match.Value.ToUpperInvariant()}' is not allowed - this console is read-only.";
        }

        sql = stripped;
        return null;
    }

    // System.Text.Json handles most CLR types, but raw bytes (rowversion, varbinary) would serialise as huge base64.
    private static object? ToJsonValue(object? value) => value switch
    {
        byte[] bytes => "0x" + Convert.ToHexString(bytes.Length > 32 ? bytes[..32] : bytes) + (bytes.Length > 32 ? "..." : string.Empty),
        _ => value,
    };
}
