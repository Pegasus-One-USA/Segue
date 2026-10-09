using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Abstractions.Tabular;
using FHIRBridge.Application.Services.Tabular;
using FHIRBridge.Integration.Sql;
using FHIRBridge.SharedKernel.Exceptions;
using MySqlConnector;
using Npgsql;

namespace FHIRBridge.Infrastructure.Tabular;

/// <summary>
/// Reads Tabular source rows: an uploaded CSV, decrypted in memory only, or a SQL query.
///
/// <para><b>SQL is read-only three ways.</b> Only one SELECT (or WITH … SELECT) statement is accepted, with no
/// statement separators and none of the keywords that change data or schema; it runs inside a transaction that is
/// always rolled back, and on PostgreSQL and MySQL that transaction is declared READ ONLY, so the database itself
/// refuses a write. A read-only database login is still the right setup, and the form says so.</para>
///
/// <para><b>PHI.</b> Cell values never reach a log or an error message; errors carry row numbers and column names.</para>
/// </summary>
public sealed partial class TabularRowReader : ITabularRowReader
{
    private const int CommandTimeoutSeconds = 60;

    private readonly ITabularSourceFileRepository _files;
    private readonly IPhiFieldEncryptor _encryptor;
    private readonly ISecretProvider _secrets;

    // One opener per engine, never a switch: a new engine is one more entry.
    private static readonly IReadOnlyDictionary<string, SqlEngine> Engines = new Dictionary<string, SqlEngine>(StringComparer.Ordinal)
    {
        ["sqlserver"] = new(
            async (connectionString, ct) => await SqlServerConnectionFactory.OpenConnectionAsync(connectionString, ct),
            BeginReadOnly: null),
        ["postgresql"] = new(
            async (connectionString, ct) =>
            {
                var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync(ct);
                return connection;
            },
            BeginReadOnly: "SET TRANSACTION READ ONLY"),
        ["mysql"] = new(
            async (connectionString, ct) =>
            {
                var connection = new MySqlConnection(connectionString);
                await connection.OpenAsync(ct);
                return connection;
            },
            BeginReadOnly: null,
            StartTransactionSql: "START TRANSACTION READ ONLY"),
    };

    public TabularRowReader(ITabularSourceFileRepository files, IPhiFieldEncryptor encryptor, ISecretProvider secrets)
    {
        _files = files;
        _encryptor = encryptor;
        _secrets = secrets;
    }

    public async Task<TabularRows> ReadFileAsync(Guid fileId, int maxRows, CancellationToken cancellationToken)
    {
        var file = await _files.GetAsync(fileId, cancellationToken)
            ?? throw new BusinessRuleException("The uploaded CSV for this source no longer exists. Upload it again.");
        return CsvTable.Parse(_encryptor.Decrypt(file.EncryptedContent), Math.Max(maxRows, 1));
    }

    public async Task<TabularRows> ReadSqlAsync(TabularSqlQuery query, int maxRows, CancellationToken cancellationToken)
    {
        if (!Engines.TryGetValue(query.Engine, out var engine))
        {
            throw new BusinessRuleException("Choose SQL Server, PostgreSQL or MySQL.");
        }

        var sql = ValidateQuery(query.Query);
        if (string.IsNullOrWhiteSpace(query.ConnectionSecret.SecretName))
        {
            throw new BusinessRuleException("Save the database connection for this source first.");
        }

        var connectionString = await _secrets.GetSecretAsync(query.ConnectionSecret, cancellationToken);
        await using var connection = await engine.OpenAsync(connectionString, cancellationToken);

        DbTransaction? transaction = null;
        if (engine.StartTransactionSql is { } start)
        {
            await ExecuteAsync(connection, null, start, cancellationToken);
        }
        else
        {
            transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            if (engine.BeginReadOnly is { } readOnly)
            {
                await ExecuteAsync(connection, transaction, readOnly, cancellationToken);
            }
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.CommandTimeout = CommandTimeoutSeconds;
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleResult, cancellationToken);

            var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
            var duplicate = columns.GroupBy(c => c, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            if (duplicate is not null)
            {
                throw new BusinessRuleException($"The query returns the column '{duplicate.Key}' more than once. Alias each column uniquely.");
            }

            var rows = new List<IReadOnlyDictionary<string, string?>>();
            var truncated = false;
            while (await reader.ReadAsync(cancellationToken))
            {
                if (rows.Count >= maxRows)
                {
                    truncated = true;
                    break;
                }

                var row = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < columns.Count; i++)
                {
                    row[columns[i]] = await reader.IsDBNullAsync(i, cancellationToken) ? null : Format(reader.GetValue(i));
                }

                rows.Add(row);
            }

            return new TabularRows(columns, rows, truncated);
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                await transaction.DisposeAsync();
            }
            else
            {
                await ExecuteAsync(connection, null, "ROLLBACK", CancellationToken.None);
            }
        }
    }

    /// <summary>One SELECT, nothing else. Comments are stripped first so they cannot hide a second statement.</summary>
    public static string ValidateQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new BusinessRuleException("Enter the SELECT query that returns the rows.");
        }

        var withoutComments = BlockComment().Replace(LineComment().Replace(query, " "), " ").Trim().TrimEnd(';').Trim();
        var withoutLiterals = StringLiteral().Replace(withoutComments, "''");
        if (!StartsWithSelect().IsMatch(withoutLiterals))
        {
            throw new BusinessRuleException("Only a SELECT query (optionally starting with WITH) can read rows.");
        }

        if (withoutLiterals.Contains(';'))
        {
            throw new BusinessRuleException("Enter a single SELECT statement, without ';' between statements.");
        }

        if (ForbiddenKeyword().Match(withoutLiterals) is { Success: true } forbidden)
        {
            throw new BusinessRuleException($"The query may only read data; '{forbidden.Value.ToUpperInvariant()}' is not allowed.");
        }

        return withoutComments;
    }

    private static string Format(object value) => value switch
    {
        DateTime dateTime => dateTime.TimeOfDay == TimeSpan.Zero
            ? dateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : dateTime.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
        DateTimeOffset offset => offset.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        bool flag => flag ? "true" : "false",
        byte[] bytes => Convert.ToBase64String(bytes),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static async Task ExecuteAsync(DbConnection connection, DbTransaction? transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.CommandTimeout = CommandTimeoutSeconds;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record SqlEngine(
        Func<string, CancellationToken, Task<DbConnection>> OpenAsync,
        string? BeginReadOnly,
        string? StartTransactionSql = null);

    [GeneratedRegex(@"--[^\r\n]*")]
    private static partial Regex LineComment();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex BlockComment();

    [GeneratedRegex(@"'(?:[^']|'')*'")]
    private static partial Regex StringLiteral();

    [GeneratedRegex(@"^\(*\s*(select|with)\b", RegexOptions.IgnoreCase)]
    private static partial Regex StartsWithSelect();

    // Statement keywords only: REPLACE(), SET and COMMENT also appear in ordinary reads (a function, a column
    // name), and the read-only transaction is the backstop for anything a keyword list misses.
    [GeneratedRegex(@"\b(insert|update|delete|merge|drop|alter|create|truncate|exec|execute|grant|revoke|into|call|copy|vacuum|attach|pragma|rename)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ForbiddenKeyword();
}
