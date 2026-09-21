using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Governance;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Provider-agnostic relational destination writer built on ADO.NET (<see cref="DbConnection"/>). The customer owns
/// the destination schema: the target table must already exist and every written column must be one the mapping
/// profile explicitly maps. Writes mapped records in Insert, Upsert, or Update mode. Upsert and Update both require
/// one mapped field flagged <see cref="MappingField.IsUpsertKey"/>. Upsert is implemented as a portable
/// delete-by-key + insert within a transaction (last-write-wins) so it works on any dialect without requiring a
/// pre-existing unique index; Update issues a plain <c>UPDATE ... WHERE key = @key</c> and never inserts. Concrete
/// subclasses supply the dialect (connection, identifier quoting, type mapping, table-existence check). Mirrors
/// <see cref="MappedSqlServerDestinationWriter"/> for SQL Server / Azure SQL.
/// </summary>
public abstract partial class RelationalDestinationWriterBase : IConfiguredDestinationWriter
{
    private readonly ISecretProvider _secretProvider;
    private readonly IGlobalExceptionManager? _exceptionManager;

    protected RelationalDestinationWriterBase(ISecretProvider secretProvider, IGlobalExceptionManager? exceptionManager = null)
    {
        _secretProvider = secretProvider;
        _exceptionManager = exceptionManager;
    }

    // --- Dialect hooks ---
    protected abstract DbConnection CreateConnection(string connectionString);
    protected abstract string DefaultSchema { get; }
    protected abstract string Quote(string identifier);
    protected abstract string ColumnType(MappingValueType valueType);
    protected abstract string BuildTableExistsSql(string schema, string table);

    /// <summary>
    /// Dialect-specific multi-row upsert statement (MySQL's <c>ON DUPLICATE KEY UPDATE</c>, PostgreSQL's
    /// <c>ON CONFLICT ... DO UPDATE</c>) — unlike <see cref="DeleteByKeyAsync"/>+<see cref="InsertRecordAsync"/>'s
    /// portable-across-any-dialect approach, this trades that portability for one round trip per batch instead of
    /// two per record. See <see cref="UpsertBatchAsync"/>'s doc comment for why that tradeoff is safe: a dialect
    /// whose target table has no unique/primary key constraint on <paramref name="keyColumn"/> (PostgreSQL requires
    /// one for <c>ON CONFLICT</c> to even parse) throws a <see cref="DbException"/> here, which the caller catches
    /// and falls back to the original per-record path for — so this never silently changes behavior, only speed,
    /// for whichever tables actually have the constraint this needs.
    /// </summary>
    protected abstract string BuildBatchUpsertSql(
        string qualifiedTable, IReadOnlyList<string> columns, string keyColumn, IReadOnlyList<string> rowValueClauses);

    /// <summary>Qualified <c>schema.table</c> (or just the quoted table when the dialect has no schemas).</summary>
    protected virtual string QualifiedName(string schema, string table)
        => string.IsNullOrEmpty(schema) ? Quote(table) : $"{Quote(schema)}.{Quote(table)}";

    public async Task<DestinationWriteResult> WriteAsync(
        DestinationConfiguration destination,
        MappingProfile mappingProfile,
        IReadOnlyCollection<MappedDestinationRecord> records,
        PipelineWriteContext context,
        CancellationToken cancellationToken)
    {
        if (records.Count == 0)
        {
            return new DestinationWriteResult(0);
        }

        var connectionString = await _secretProvider.GetSecretAsync(destination.SecretReference, cancellationToken);
        var target = ParseTarget(destination.Target ?? mappingProfile.DestinationObject, mappingProfile);

        await using var connection = CreateConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await EnsureTableAsync(connection, target, mappingProfile, cancellationToken);

        // Each record's write stands alone: a constraint violation (dup key, NOT NULL, truncation, conversion, ...)
        // on one record's data must not discard every other record already validated and ready to write in the
        // same batch. Only a DbException is caught here — anything else (e.g. the connection itself dying) can't
        // be recovered from per-record and is left to propagate and fail the whole route, as before.
        var recordErrors = new List<string>();
        var writtenResourceIds = new List<string?>();

        // Insert/Upsert can be written as one multi-row statement per chunk instead of one (Insert) or two
        // (Upsert's delete-by-key + insert) round trips per record — the dominant cost for a large resource type
        // is round-trip latency, not the write itself. Update-only keeps the original per-record path unchanged
        // (it's a targeted single-row operation by nature, not a bulk-load scenario).
        const int BatchSize = 200;
        var recordList = records as IReadOnlyList<MappedDestinationRecord> ?? records.ToList();

        foreach (var chunk in recordList.Chunk(BatchSize))
        {
            var canBatch = !target.UpdateOnly
                && (!target.Upsert || chunk.All(record => TryGetKeyValue(record, target.KeyColumn!, out _)));

            if (canBatch)
            {
                try
                {
                    var batchWrittenIds = target.Upsert
                        ? await UpsertBatchAsync(connection, target, chunk, cancellationToken)
                        : await InsertBatchAsync(connection, target, chunk, cancellationToken);

                    writtenResourceIds.AddRange(batchWrittenIds);
                    continue;
                }
                catch (DbException)
                {
                    // One bad record's fault (truncation, constraint violation, ...) — or, for Upsert, a target
                    // table with no unique/primary key constraint on the key column at all, which
                    // BuildBatchUpsertSql's ON CONFLICT/ON DUPLICATE KEY clause requires — must not discard the
                    // rest of this chunk. Fall through to the proven per-record path below for just this chunk.
                }
            }

            foreach (var record in chunk)
            {
                try
                {
                    await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

                    if (target.UpdateOnly)
                    {
                        // Update-only never inserts: a record with no key value has nothing to match, so it's
                        // skipped entirely rather than falling back to Insert.
                        if (TryGetKeyValue(record, target.KeyColumn!, out var updateKeyValue))
                        {
                            var rowsAffected = await UpdateRecordAsync(connection, transaction, target, record, updateKeyValue, cancellationToken);
                            await transaction.CommitAsync(cancellationToken);
                            if (rowsAffected > 0)
                            {
                                writtenResourceIds.Add(record.SourceResourceId);
                            }
                        }

                        continue;
                    }

                    if (target.Upsert && TryGetKeyValue(record, target.KeyColumn!, out var keyValue))
                    {
                        await DeleteByKeyAsync(connection, transaction, target, keyValue, cancellationToken);
                    }

                    await InsertRecordAsync(connection, transaction, target, record, cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    writtenResourceIds.Add(record.SourceResourceId);
                }
                catch (DbException exception)
                {
                    recordErrors.Add(
                        $"{record.ResourceType}/{record.SourceResourceId ?? "unknown"}: {exception.Message}");

                    if (_exceptionManager is not null)
                    {
                        await _exceptionManager.CaptureAsync(
                            exception,
                            new ExceptionContext(Module: "Destination Write", CorrelationId: context.CorrelationId),
                            cancellationToken);
                    }
                }
            }
        }

        return new DestinationWriteResult(
            writtenResourceIds.Count,
            RecordErrors: recordErrors.Count > 0 ? recordErrors : null,
            WrittenResourceIds: writtenResourceIds);
    }

    /// <summary>Multi-row INSERT for a chunk — one round trip instead of one per record.</summary>
    private async Task<List<string?>> InsertBatchAsync(
        DbConnection connection,
        RelationalTarget target,
        IReadOnlyList<MappedDestinationRecord> chunk,
        CancellationToken cancellationToken)
    {
        var columns = BuildBatchColumns(chunk);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;

        var rowValueClauses = new List<string>(chunk.Count);
        for (var rowIndex = 0; rowIndex < chunk.Count; rowIndex++)
        {
            rowValueClauses.Add(AddRowParameters(command, chunk[rowIndex], columns, rowIndex));
        }

        command.CommandText = $"INSERT INTO {QualifiedName(target.Schema, target.Table)} " +
            $"({string.Join(", ", columns.Select(Quote))}) VALUES {string.Join(", ", rowValueClauses)}";

        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return chunk.Select(record => record.SourceResourceId).ToList();
    }

    /// <summary>
    /// Multi-row upsert for a chunk, via <see cref="BuildBatchUpsertSql"/>'s dialect-specific syntax — trades the
    /// portable delete-by-key+insert-per-record approach's dialect-independence for one round trip per batch, only
    /// for chunks where every record already has a usable key value (checked by the caller in <see cref="WriteAsync"/>;
    /// a keyless record must go through the per-record path's own insert-instead-of-upsert handling, which this
    /// batched form doesn't replicate).
    /// </summary>
    private async Task<List<string?>> UpsertBatchAsync(
        DbConnection connection,
        RelationalTarget target,
        IReadOnlyList<MappedDestinationRecord> chunk,
        CancellationToken cancellationToken)
    {
        var columns = BuildBatchColumns(chunk);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;

        var rowValueClauses = new List<string>(chunk.Count);
        for (var rowIndex = 0; rowIndex < chunk.Count; rowIndex++)
        {
            rowValueClauses.Add(AddRowParameters(command, chunk[rowIndex], columns, rowIndex));
        }

        command.CommandText = BuildBatchUpsertSql(QualifiedName(target.Schema, target.Table), columns, target.KeyColumn!, rowValueClauses);

        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return chunk.Select(record => record.SourceResourceId).ToList();
    }

    /// <summary>Union of mapped columns across every record in the chunk, so every row in the batch statement
    /// aligns to the same column list regardless of which optional fields any one record happened to carry — a
    /// record missing a given column writes NULL for it, same as the per-record path would for that record alone.</summary>
    private static List<string> BuildBatchColumns(IReadOnlyList<MappedDestinationRecord> chunk) =>
        chunk.SelectMany(record => record.Values.Keys)
            .Select(ValidateIdentifier)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Adds this row's parameters to the shared batch command (index-based names — <c>@p{row}_{col}</c> —
    /// so a column name is never itself part of a parameter name) and returns the row's own
    /// "(@p0_0, @p0_1, ...)" VALUES clause fragment.</summary>
    private static string AddRowParameters(
        DbCommand command, MappedDestinationRecord record, IReadOnlyList<string> columns, int rowIndex)
    {
        var placeholders = new string[columns.Count];
        for (var columnIndex = 0; columnIndex < columns.Count; columnIndex++)
        {
            var paramName = $"p{rowIndex}_{columnIndex}";
            placeholders[columnIndex] = "@" + paramName;
            AddParameter(command, paramName, Stringify(record.Values.GetValueOrDefault(columns[columnIndex])));
        }

        return $"({string.Join(", ", placeholders)})";
    }

    private async Task EnsureTableAsync(
        DbConnection connection,
        RelationalTarget target,
        MappingProfile mappingProfile,
        CancellationToken cancellationToken)
    {
        var hasMappedFields = mappingProfile.Fields.Any(field =>
            (string.IsNullOrWhiteSpace(field.ResourceType) ||
                string.Equals(field.ResourceType, mappingProfile.ResourceType, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(field.DestinationObject) ||
                string.Equals(field.DestinationObject, mappingProfile.DestinationObject, StringComparison.OrdinalIgnoreCase)));

        if (!hasMappedFields)
        {
            throw new InvalidOperationException(
                $"Mapping profile for '{target.Schema}.{target.Table}' has no mapped fields — a SQL destination needs at least one mapped column.");
        }

        await using var command = connection.CreateCommand();
        command.CommandText = BuildTableExistsSql(target.Schema, target.Table);
        var result = await command.ExecuteScalarAsync(cancellationToken);

        if (result is null or DBNull)
        {
            throw new InvalidOperationException(
                $"Destination table '{QualifiedName(target.Schema, target.Table)}' does not exist. Create it in your database before running this pipeline.");
        }
    }

    private async Task InsertRecordAsync(
        DbConnection connection,
        DbTransaction transaction,
        RelationalTarget target,
        MappedDestinationRecord record,
        CancellationToken cancellationToken)
    {
        var columns = record.Values.Keys
            .Select(ValidateIdentifier)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (columns.Count == 0)
        {
            return;
        }

        var sql = $"INSERT INTO {QualifiedName(target.Schema, target.Table)} " +
                  $"({string.Join(", ", columns.Select(Quote))}) VALUES " +
                  $"({string.Join(", ", columns.Select(c => "@" + c))})";

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var column in columns)
        {
            AddParameter(command, column, Stringify(record.Values[column]));
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task DeleteByKeyAsync(
        DbConnection connection,
        DbTransaction transaction,
        RelationalTarget target,
        object? keyValue,
        CancellationToken cancellationToken)
    {
        var sql = $"DELETE FROM {QualifiedName(target.Schema, target.Table)} " +
                  $"WHERE {Quote(target.KeyColumn!)} = @KeyValue";

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        AddParameter(command, "KeyValue", Stringify(keyValue));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Updates the matching row by <see cref="RelationalTarget.KeyColumn"/> only — never inserts. Called
    /// only when the record actually has a key value (see <see cref="WriteAsync"/>); a record with no matching row
    /// is simply left unwritten, which is exactly what "Update only" means.</summary>
    private async Task<int> UpdateRecordAsync(
        DbConnection connection,
        DbTransaction transaction,
        RelationalTarget target,
        MappedDestinationRecord record,
        object? keyValue,
        CancellationToken cancellationToken)
    {
        var columns = record.Values.Keys
            .Select(ValidateIdentifier)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(column => !string.Equals(column, target.KeyColumn, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (columns.Count == 0)
        {
            return 0;
        }

        var setClause = string.Join(", ", columns.Select(column => $"{Quote(column)} = @{column}"));
        var sql = $"UPDATE {QualifiedName(target.Schema, target.Table)} SET {setClause} " +
                  $"WHERE {Quote(target.KeyColumn!)} = @KeyValue";

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var column in columns)
        {
            AddParameter(command, column, Stringify(record.Values[column]));
        }
        AddParameter(command, "KeyValue", Stringify(keyValue));

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@" + name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    // .NET's default ToString() for DateOnly ("MM/dd/yyyy") isn't a date literal any SQL dialect accepts, so
    // DateOnly/DateTimeOffset still need their own explicit, dialect-portable string format (ISO 8601) rather
    // than falling through to Convert.ToString. DateTime, bool and the numeric CLR types are passed through as
    // their native CLR type so each provider (Npgsql/MySqlConnector/SqlClient) can infer the correct parameter
    // type for whatever real column type the destination schema actually has (date/datetime/boolean/bit/tinyint/
    // int/decimal), instead of being coerced to a string that a strictly-typed column will reject. This matters
    // most for PostgreSQL: unlike SQL Server, Npgsql sends an untyped string as a `text` parameter and Postgres
    // refuses to implicitly cast text to integer/numeric on INSERT (42804), even when the string is numeric —
    // e.g. JsonMappingEngine.ConvertInteger already hands back a real int for an Integer-typed field, and this
    // switch used to stringify it right back before it ever reached the provider.
    // internal, not private: MappedSqlServerDestinationWriter.AddColumnParameter follows the same seam for its
    // own unit tests (see MappedSqlServerDestinationWriterTests) — a direct, reflection-free regression guard on
    // exactly the arms that decide what gets sent to the provider is cheaper than exercising it through a real
    // connection, and it's what stops someone "tidying" the numeric arms back into the string fallback unnoticed.
    internal static object Stringify(object? value) => value switch
    {
        null => DBNull.Value,
        DateTime dateTime => dateTime,
        DateTimeOffset dateTimeOffset => dateTimeOffset.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        DateOnly dateOnly => dateOnly.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        bool boolean => boolean,
        int intValue => intValue,
        long longValue => longValue,
        short shortValue => shortValue,
        decimal decimalValue => decimalValue,
        double doubleValue => doubleValue,
        float floatValue => floatValue,
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    private static bool TryGetKeyValue(MappedDestinationRecord record, string keyColumn, out object? keyValue)
        => record.Values.TryGetValue(keyColumn, out keyValue) && keyValue is not null;

    private RelationalTarget ParseTarget(string destinationObject, MappingProfile mappingProfile)
    {
        // A stored destination object may carry a ';mode=<writeMode>' (or legacy '?key=') suffix — neither is part
        // of the table identifier, so both must be stripped before any '.'-split / identifier validation runs.
        // Splitting on ';' first (mirroring MappedSqlServerDestinationWriter.ParseDestinationTarget) matters most
        // for a schema-less dialect like MySQL, where a bare "Table;mode=update" has no '.' to isolate the suffix.
        var objectAndOptions = destinationObject.Trim().Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var name = objectAndOptions.Length > 0 ? objectAndOptions[0] : string.Empty;
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var queryIndex = name.IndexOf('?', StringComparison.Ordinal);
        if (queryIndex >= 0)
        {
            foreach (var option in name[(queryIndex + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                AddOption(options, option);
            }

            name = name[..queryIndex];
        }

        foreach (var option in objectAndOptions.Skip(1))
        {
            AddOption(options, option);
        }

        var parts = name.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var (schema, table) = parts.Length switch
        {
            1 => (DefaultSchema, ValidateIdentifier(parts[0])),
            2 => (ValidateIdentifier(parts[0]), ValidateIdentifier(parts[1])),
            _ => throw new InvalidOperationException("Destination object must be 'Table' or 'Schema.Table'.")
        };

        var mode = options.TryGetValue("mode", out var configuredMode) ? configuredMode.Trim().ToLowerInvariant() : null;
        var upsert = mode == "upsert";
        var updateOnly = mode == "update";

        // Prefer the structured IsUpsertKey flag; fall back to the legacy '?key=' option so destinations configured
        // before the mapping UI grows an IsUpsertKey control (docs/backend/11-destination-schema-ownership-plan.md
        // section 4 item 5) keep working. Remove the fallback once that UI work lands.
        var keyColumn = ResolveUpsertKeyColumn(mappingProfile)
            ?? (options.TryGetValue("key", out var configuredKey) ? ValidateIdentifier(configuredKey) : null);

        if (upsert && keyColumn is null)
        {
            throw new InvalidOperationException(
                $"Destination '{schema}.{table}' is configured for Upsert mode but no mapped field is designated " +
                "as the upsert key. Mark one mapped field's IsUpsertKey in the mapping profile.");
        }

        if (updateOnly && keyColumn is null)
        {
            throw new InvalidOperationException(
                $"Destination '{schema}.{table}' is configured for Update mode but no mapped field is designated " +
                "as the update key. Mark one mapped field's IsUpsertKey in the mapping profile.");
        }

        return new RelationalTarget(schema, table, upsert, updateOnly, keyColumn);
    }

    private static void AddOption(IDictionary<string, string> options, string option)
    {
        var equalsIndex = option.IndexOf('=', StringComparison.Ordinal);
        if (equalsIndex <= 0)
        {
            return;
        }

        options[option[..equalsIndex].Trim()] = option[(equalsIndex + 1)..].Trim();
    }

    /// <summary>
    /// The mapped field marked <see cref="MappingField.IsUpsertKey"/> for this profile's resource/destination-object
    /// scope, if any — derived from the mapping config rather than a second, independently-configured value.
    /// </summary>
    private static string? ResolveUpsertKeyColumn(MappingProfile mappingProfile)
    {
        var keyField = mappingProfile.Fields.FirstOrDefault(field =>
            field.IsUpsertKey &&
            field.IsEnabled &&
            (string.IsNullOrWhiteSpace(field.ResourceType) ||
                string.Equals(field.ResourceType, mappingProfile.ResourceType, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(field.DestinationObject) ||
                string.Equals(field.DestinationObject, mappingProfile.DestinationObject, StringComparison.OrdinalIgnoreCase)));

        return keyField is null ? null : ValidateIdentifier(keyField.TargetField);
    }

    protected static string ValidateIdentifier(string identifier)
    {
        if (!SqlIdentifierRegex().IsMatch(identifier))
        {
            throw new InvalidOperationException($"'{identifier}' is not a valid SQL identifier.");
        }

        return identifier;
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex SqlIdentifierRegex();

    private sealed record RelationalTarget(
        string Schema,
        string Table,
        bool Upsert,
        bool UpdateOnly,
        string? KeyColumn);
}
