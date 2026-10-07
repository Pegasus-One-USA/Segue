using System.Data;
using System.Data.Common;
using System.Globalization;
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
/// statement separators, none of the keywords that change data or schema and none of the functions that run text on
/// another server (<see cref="TabularSqlGuard"/>); it runs inside a transaction that is always rolled back; and the
/// database itself refuses a write: on PostgreSQL and MySQL the transaction is declared READ ONLY, and on SQL Server,
/// which has no such transaction, a login that can write is refused before the query runs. The connection must be
/// one the Tabular form saved.</para>
///
/// <para><b>PHI.</b> Cell values never reach a log or an error message; errors carry row numbers and column names.</para>
/// </summary>
public sealed class TabularRowReader : ITabularRowReader
{
    private const int CommandTimeoutSeconds = 60;

    private readonly ITabularSourceFileRepository _files;
    private readonly IPhiFieldEncryptor _encryptor;
    private readonly ISecretProvider _secrets;
    private readonly ITenantSecretVaultResolver _vaults;

    // One opener per engine, never a switch: a new engine is one more entry.
    private static readonly IReadOnlyDictionary<string, SqlEngine> Engines = new Dictionary<string, SqlEngine>(StringComparer.Ordinal)
    {
        // SQL Server has no read-only transaction, so the login itself must not be able to write: checked on every
        // connection (VerifyReadOnlySql), not just recommended.
        ["sqlserver"] = new(
            async (connectionString, ct) => await SqlServerConnectionFactory.OpenConnectionAsync(connectionString, ct),
            BeginReadOnly: null,
            VerifyReadOnlySql: """
                SELECT CASE WHEN IS_SRVROLEMEMBER('sysadmin') = 1
                    OR HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'INSERT') = 1
                    OR HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'UPDATE') = 1
                    OR HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'DELETE') = 1
                    OR HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'ALTER') = 1
                    OR HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'EXECUTE') = 1
                    OR HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'CONTROL') = 1
                THEN 1 ELSE 0 END
                """),
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

    public TabularRowReader(
        ITabularSourceFileRepository files,
        IPhiFieldEncryptor encryptor,
        ISecretProvider secrets,
        ITenantSecretVaultResolver vaults)
    {
        _files = files;
        _encryptor = encryptor;
        _secrets = secrets;
        _vaults = vaults;
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

        var sql = TabularSqlGuard.ValidateQuery(query.Query, query.Engine);
        if (string.IsNullOrWhiteSpace(query.ConnectionSecret.SecretName))
        {
            throw new BusinessRuleException("Save the database connection for this source first.");
        }

        // Before the secret is read: only a connection the Tabular form saved may be used.
        TabularSqlGuard.RequireTabularConnectionSecret(query.ConnectionSecret, query.Engine, _vaults);
        var connectionString = await _secrets.GetSecretAsync(query.ConnectionSecret, cancellationToken);
        await using var connection = await engine.OpenAsync(connectionString, cancellationToken);
        if (engine.VerifyReadOnlySql is { } verify)
        {
            await using var check = connection.CreateCommand();
            check.CommandText = verify;
            check.CommandTimeout = CommandTimeoutSeconds;
            if (Convert.ToInt32(await check.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 0)
            {
                throw new BusinessRuleException(
                    "This database login can change data. Use a read-only login (for SQL Server, a user with db_datareader only).");
            }
        }

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

    /// <summary>One SELECT, nothing else (see <see cref="TabularSqlGuard"/>); returns the text that runs.</summary>
    public static string ValidateQuery(string? query, string engine = "sqlserver") => TabularSqlGuard.ValidateQuery(query, engine);

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
        string? StartTransactionSql = null,
        string? VerifyReadOnlySql = null);
}
