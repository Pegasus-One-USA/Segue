using System.Text.Json;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Governance;
using FHIRBridge.Integration.Sql;
using Microsoft.Data.SqlClient;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Writes mapped records to SQL Server / Azure SQL. The customer owns the destination schema: the target table must
/// already exist (created directly, or via the Mapping Config Import wizard's own explicit DDL), and only the
/// mapped destination columns are ever written. Supports Insert, Upsert (MERGE on the mapped field flagged
/// <see cref="MappingField.IsUpsertKey"/>, or "SourceResourceId" by default), Update-only (updates the matching
/// row by that same key only, never inserts), and CDC write modes — plus FK-aware reference-lookup resolution
/// and child-table (SeparateDestination) writes for a mapping profile spanning more than one table.
/// Note: "CDC" here is an application-level change-history approximation — each write is mirrored into a
/// companion <c>{Table}_Cdc</c> table — and is NOT SQL Server's native Change Data Capture feature.
/// TODO: CDC mode still auto-creates its companion table, which conflicts with the customer-owned-schema model;
/// its fate (retire vs. require a customer-provisioned table) is an open decision (see
/// docs/backend/11-destination-schema-ownership-plan.md section 3.A.3).
/// </summary>
public sealed class MappedSqlServerDestinationWriter : IConfiguredDestinationWriter
{
    // System-managed columns the writer emits when they exist on the target table. A mapping field targeting
    // one of these is ignored (the system value wins) so the generated INSERT/MERGE never declares a column
    // twice. Tables created by the Mapping Config Import feature don't have these columns at all — the writer
    // detects that per-table (see GetExistingColumnNamesAsync) and simply omits whichever are absent, rather
    // than assuming every target table has the full FHIRBridge-managed column set.
    private static readonly HashSet<string> ReservedColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "FHIRBridgeRowId", "PipelineRunId", "ResourceType", "SourceResourceId", "WrittenOnUtc", "LastUpdatedOnUtc"
    };

    private static readonly string[] InsertSystemColumns = ["PipelineRunId", "ResourceType", "SourceResourceId", "WrittenOnUtc"];
    private static readonly string[] UpsertSystemColumns = ["PipelineRunId", "ResourceType", "SourceResourceId", "WrittenOnUtc", "LastUpdatedOnUtc"];
    private static readonly IReadOnlyDictionary<string, object?> EmptyColumnValues = new Dictionary<string, object?>();

    private readonly ISecretProvider _secretProvider;
    private readonly IGlobalExceptionManager? _exceptionManager;

    public MappedSqlServerDestinationWriter(ISecretProvider secretProvider, IGlobalExceptionManager? exceptionManager = null)
    {
        _secretProvider = secretProvider;
        _exceptionManager = exceptionManager;
    }

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

        var connectionString = await _secretProvider.GetSecretAsync(
            destination.SecretReference,
            cancellationToken);
        var target = ParseDestinationTarget(
            destination.Target ?? mappingProfile.DestinationObject,
            mappingProfile);

        await using var connection = await SqlServerConnectionFactory.OpenConnectionAsync(connectionString, cancellationToken);

        await EnsureTableAsync(
            connection,
            target.SchemaName,
            target.TableName,
            mappingProfile,
            cancellationToken);

        if (target.WriteMode == SqlDestinationWriteMode.Cdc)
        {
            await EnsureCdcTableAsync(
                connection,
                target.SchemaName,
                target.TableName,
                cancellationToken);
        }

        var existingColumns = await GetExistingColumnNamesAsync(connection, target.SchemaName, target.TableName, cancellationToken);

        // A table created outside EnsureTableAsync (e.g. by the Mapping Config Import feature) never has the
        // system SourceResourceId column, so the configured/default key column for Upsert/Update mode won't
        // exist on it. See ResolveNaturalKeyColumn for why the fallback is a mapped "$.id" field, not the
        // table's own primary key.
        var needsKeyFallback = target.WriteMode is SqlDestinationWriteMode.Upsert or SqlDestinationWriteMode.Update
            && !existingColumns.Contains(target.KeyColumn);
        var keyColumn = needsKeyFallback
            ? ResolveNaturalKeyColumn(mappingProfile, target.TableName) ?? target.KeyColumn
            : target.KeyColumn;

        // Each record's write stands alone: a constraint violation (dup key, NOT NULL, truncation, conversion, ...)
        // on one record's data must not discard every other record already validated and ready to write in the same
        // batch. Only a SqlException is caught here — anything else (e.g. the connection itself dying) can't be
        // recovered from per-record and is left to propagate and fail the whole route, as before.
        var recordErrors = new List<string>();
        var writtenResourceIds = new List<string?>();

        foreach (var record in records)
        {
            try
            {
                var resolvedRecord = await ResolveReferenceLookupsAsync(connection, record, cancellationToken);

                IReadOnlyDictionary<string, object?> capturedParentColumns;
                var written = true;
                switch (target.WriteMode)
                {
                    case SqlDestinationWriteMode.Upsert:
                        capturedParentColumns = await UpsertRecordAsync(
                            connection, target.SchemaName, target.TableName, resolvedRecord, keyColumn, existingColumns, context, cancellationToken);
                        break;
                    case SqlDestinationWriteMode.Update:
                        if (!TryGetKeyValue(resolvedRecord, keyColumn, out _))
                        {
                            // Nothing to match on — "Update only" leaves an unmatched/keyless record unwritten.
                            written = false;
                            capturedParentColumns = EmptyColumnValues;
                            break;
                        }

                        capturedParentColumns = await UpdateOnlyRecordAsync(
                            connection, target.SchemaName, target.TableName, resolvedRecord, keyColumn, existingColumns, context, cancellationToken);
                        break;
                    case SqlDestinationWriteMode.Cdc:
                        capturedParentColumns = await InsertRecordAsync(
                            connection, target.SchemaName, target.TableName, resolvedRecord, existingColumns, context, cancellationToken);
                        await InsertCdcRecordAsync(connection, target.SchemaName, target.TableName, resolvedRecord, cancellationToken);
                        break;
                    default:
                        capturedParentColumns = await InsertRecordAsync(
                            connection, target.SchemaName, target.TableName, resolvedRecord, existingColumns, context, cancellationToken);
                        break;
                }

                if (written && resolvedRecord.ChildTables is { Count: > 0 } childTables)
                {
                    await WriteChildTablesAsync(
                        connection, resolvedRecord, childTables, capturedParentColumns,
                        deleteExistingChildRows: target.WriteMode is SqlDestinationWriteMode.Upsert or SqlDestinationWriteMode.Update,
                        cancellationToken);
                }

                if (written)
                {
                    writtenResourceIds.Add(record.SourceResourceId);
                }
            }
            catch (SqlException exception)
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

        return new DestinationWriteResult(
            writtenResourceIds.Count,
            RecordErrors: recordErrors.Count > 0 ? recordErrors : null,
            WrittenResourceIds: writtenResourceIds);
    }

    private static async Task EnsureTableAsync(
        SqlConnection connection,
        string schemaName,
        string tableName,
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
                $"Mapping profile for '{schemaName}.{tableName}' has no mapped fields — a SQL destination needs at least one mapped column.");
        }

        await using var command = new SqlCommand("SELECT OBJECT_ID(@ObjectId, N'U')", connection);
        command.Parameters.AddWithValue("@ObjectId", $"[{schemaName}].[{tableName}]");
        var objectId = await command.ExecuteScalarAsync(cancellationToken);

        if (objectId is null or DBNull)
        {
            throw new InvalidOperationException(
                $"Destination table '{schemaName}.{tableName}' does not exist. Create it in your database before " +
                "running this pipeline (directly, or via the mapping-config import wizard).");
        }
    }

    /// <summary>
    /// Reads the real column set of the target table. Used to tolerate tables the Mapping Config Import
    /// feature created directly (a PK plus mapped columns only, no reserved system columns) instead of
    /// assuming every target table has the full FHIRBridge-managed column set.
    /// </summary>
    private static async Task<HashSet<string>> GetExistingColumnNamesAsync(
        SqlConnection connection, string schemaName, string tableName, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@schema", schemaName);
        command.Parameters.AddWithValue("@table", tableName);

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(reader.GetString(0));
        }

        return columns;
    }

    /// <summary>
    /// Used as the Upsert/Update join key when the configured/default key column (normally SourceResourceId)
    /// doesn't exist on the target table — see the comment in <see cref="WriteAsync"/>. Deliberately NOT the
    /// table's own primary key: an auto-generated IDENTITY PK is never present in a mapped record's Values and
    /// never equal to the resource's own id, so it can never actually match on Upsert/Update — silently
    /// degrading "Upsert" into "always insert" (the exact bug this replaces). Instead, use whichever root-level
    /// mapped field's JsonPath is exactly "$.id" — the FHIR resource's own identifier, guaranteed by the FHIR
    /// spec to exist exactly once at the resource root, and always sourced identically to
    /// <see cref="MappedDestinationRecord.SourceResourceId"/>. Returns null if no such field is mapped; the
    /// caller then leaves the key column as configured, so a missing/misconfigured key fails loudly (an
    /// "Invalid column name" SQL error) instead of silently duplicating data forever.
    /// </summary>
    /// <param name="destinationTableName">The already-parsed plain table name (<see cref="SqlDestinationTarget.TableName"/>),
    /// NOT <c>mappingProfile.DestinationObject</c> — for a Runtime-DAG destination node, that property can be the
    /// compound SQL target descriptor (e.g. "dbo.Patient;mode=upsert") rather than the plain object name every
    /// individual field's own <c>DestinationObject</c> actually carries (e.g. "Patient"), which would make this
    /// lookup silently fail to match any field.</param>
    private static string? ResolveNaturalKeyColumn(MappingProfile mappingProfile, string destinationTableName) =>
        mappingProfile.Fields
            .Where(field =>
                string.IsNullOrWhiteSpace(field.DestinationObject) ||
                string.Equals(field.DestinationObject, destinationTableName, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(field => IsRootIdJsonPath(field.JsonPath))
            ?.TargetField;

    /// <summary>
    /// True when a field's JsonPath resolves to the FHIR resource's own root "id". Two conventions reach this
    /// method for the same field: a <see cref="MappingProfile"/> loaded from the DB (built via the
    /// mapping-profiles/import wizard) always uses the "$."-prefixed form ("$.id"), while a MappingProfile
    /// assembled inline from a Runtime-DAG node's own embedded field list (built via /workflows/build, see
    /// <c>DestinationNodeExecutor.CreateMappingProfile</c>) carries the unprefixed form ("id"). Both mean the
    /// same thing and must both match, or this fallback silently fails for one of the two paths.
    /// </summary>
    private static bool IsRootIdJsonPath(string? jsonPath)
    {
        if (string.IsNullOrWhiteSpace(jsonPath))
        {
            return false;
        }

        var normalized = jsonPath.StartsWith("$.", StringComparison.Ordinal) ? jsonPath[2..] : jsonPath;
        return string.Equals(normalized, "id", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves every <see cref="MappedDestinationRecord.ReferenceLookups"/> entry (e.g. Observation.PatientId,
    /// sourced from "$.subject.reference") against the table it actually points at, and returns a record whose
    /// <see cref="MappedDestinationRecord.Values"/> carry the resolved real primary key instead of the raw FHIR
    /// reference id the mapping engine extracted. Requires the referenced row to already exist — the Runtime-DAG
    /// destination executor orders resource-type groups so a referenced table's group is written before any
    /// group that references it, within one destination write.
    /// </summary>
    private static async Task<MappedDestinationRecord> ResolveReferenceLookupsAsync(
        SqlConnection connection, MappedDestinationRecord record, CancellationToken cancellationToken)
    {
        if (record.ReferenceLookups is not { Count: > 0 } lookups)
        {
            return record;
        }

        var resolvedValues = new Dictionary<string, object?>(record.Values, StringComparer.OrdinalIgnoreCase);
        foreach (var lookup in lookups)
        {
            if (string.IsNullOrWhiteSpace(lookup.ReferenceId))
            {
                // The source resource had no reference at that path — nothing to resolve; the target column is
                // left as whatever JsonMappingEngine already put there (null), so a NOT NULL column fails with
                // its own clear error rather than this method guessing a value.
                continue;
            }

            var (schema, table) = ParseDestinationObject(lookup.LookupTable);
            var lookupColumn = ValidateIdentifier(lookup.LookupKeyColumn);
            // The referenced table's own natural/business key doubles as its identity here (these destination
            // schemas have no generic surrogate "Id" column) — selecting the same column back both confirms the
            // row exists and gives the exact value to store as the FK, without assuming a column that isn't there.
            var sql = $"SELECT TOP (1) [{lookupColumn}] FROM [{schema}].[{table}] WHERE [{lookupColumn}] = @referenceId;";

            await using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@referenceId", lookup.ReferenceId);
            var resolved = await command.ExecuteScalarAsync(cancellationToken);

            if (resolved is null or DBNull)
            {
                throw new InvalidOperationException(
                    $"Cannot resolve '{lookup.TargetField}': no row in [{schema}].[{table}] has " +
                    $"[{lookupColumn}] = '{lookup.ReferenceId}'. The referenced resource must be written before " +
                    "this one, in the same destination write.");
            }

            resolvedValues[lookup.TargetField] = resolved;
        }

        return record with { Values = resolvedValues };
    }

    private static async Task<IReadOnlyDictionary<string, object?>> InsertRecordAsync(
        SqlConnection connection,
        string schemaName,
        string tableName,
        MappedDestinationRecord record,
        IReadOnlySet<string> existingColumns,
        PipelineWriteContext context,
        CancellationToken cancellationToken)
    {
        var fieldNames = record.Values.Keys
            .Where(key => !ReservedColumns.Contains(key))
            .Select(ValidateIdentifier)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var systemColumns = InsertSystemColumns.Where(existingColumns.Contains).ToList();
        var columns = systemColumns.Concat(fieldNames).ToList();
        var outputColumns = ResolveOutputColumns(record);

        var sql = $"""
            INSERT INTO [{schemaName}].[{tableName}]
            (
                {string.Join(", ", columns.Select(column => $"[{column}]"))}
            )
            {(outputColumns.Count > 0 ? $"OUTPUT {string.Join(", ", outputColumns.Select(c => $"INSERTED.[{c}]"))}" : string.Empty)}
            VALUES
            (
                {string.Join(", ", columns.Select(column => $"@{column}"))}
            );
            """;

        await using var command = new SqlCommand(sql, connection);
        foreach (var column in columns)
        {
            command.Parameters.AddWithValue($"@{column}", ResolveColumnValue(record, column, context) ?? DBNull.Value);
        }

        if (outputColumns.Count == 0)
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
            return EmptyColumnValues;
        }

        return await ExecuteCapturingOutputAsync(command, outputColumns, cancellationToken);
    }

    private static async Task<IReadOnlyDictionary<string, object?>> UpsertRecordAsync(
        SqlConnection connection,
        string schemaName,
        string tableName,
        MappedDestinationRecord record,
        string keyColumn,
        IReadOnlySet<string> existingColumns,
        PipelineWriteContext context,
        CancellationToken cancellationToken)
    {
        if (!TryGetKeyValue(record, keyColumn, out _))
        {
            return await InsertRecordAsync(connection, schemaName, tableName, record, existingColumns, context, cancellationToken);
        }

        return await MergeRecordAsync(
            connection, schemaName, tableName, record, keyColumn, existingColumns, context, insertWhenNotMatched: true, cancellationToken);
    }

    /// <summary>Updates an existing row matched by <paramref name="keyColumn"/>; a record whose key matches
    /// nothing is left alone entirely — the opposite trade-off from <see cref="UpsertRecordAsync"/>, which
    /// always creates a new row for an unmatched key. A record with no usable key value at all has nothing to
    /// match, so (like an unmatched key) nothing is written for it.</summary>
    private static async Task<IReadOnlyDictionary<string, object?>> UpdateOnlyRecordAsync(
        SqlConnection connection,
        string schemaName,
        string tableName,
        MappedDestinationRecord record,
        string keyColumn,
        IReadOnlySet<string> existingColumns,
        PipelineWriteContext context,
        CancellationToken cancellationToken)
    {
        if (!TryGetKeyValue(record, keyColumn, out _))
        {
            return EmptyColumnValues;
        }

        return await MergeRecordAsync(
            connection, schemaName, tableName, record, keyColumn, existingColumns, context, insertWhenNotMatched: false, cancellationToken);
    }

    /// <summary>
    /// Shared MERGE construction for both Upsert and Update-only: identical column/key/OUTPUT handling either
    /// way — the only difference is whether an unmatched source row also gets inserted. Omitting the
    /// "WHEN NOT MATCHED THEN INSERT" branch is what makes Update-only genuinely update-or-nothing: SQL Server
    /// simply leaves an unmatched source row alone when that branch is absent.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, object?>> MergeRecordAsync(
        SqlConnection connection,
        string schemaName,
        string tableName,
        MappedDestinationRecord record,
        string keyColumn,
        IReadOnlySet<string> existingColumns,
        PipelineWriteContext context,
        bool insertWhenNotMatched,
        CancellationToken cancellationToken)
    {
        var fieldNames = record.Values.Keys
            .Where(key => !ReservedColumns.Contains(key))
            .Select(ValidateIdentifier)
            .ToList();
        var standardColumns = UpsertSystemColumns.Where(existingColumns.Contains).ToList();
        var columns = standardColumns.Concat(fieldNames).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var validatedKeyColumn = ValidateIdentifier(keyColumn);
        var updateColumns = columns
            .Where(column => !string.Equals(column, "WrittenOnUtc", StringComparison.OrdinalIgnoreCase))
            .Where(column => !string.Equals(column, validatedKeyColumn, StringComparison.OrdinalIgnoreCase))
            .Select(column => $"target.[{column}] = source.[{column}]")
            .ToList();
        var onClause = existingColumns.Contains("ResourceType")
            ? $"target.[ResourceType] = source.[ResourceType] AND target.[{validatedKeyColumn}] = source.[{validatedKeyColumn}]"
            : $"target.[{validatedKeyColumn}] = source.[{validatedKeyColumn}]";
        var outputColumns = ResolveOutputColumns(record);
        var insertClause = insertWhenNotMatched
            ? $"""
                WHEN NOT MATCHED THEN
                    INSERT ({string.Join(", ", columns.Select(column => $"[{column}]"))})
                    VALUES ({string.Join(", ", columns.Select(column => $"source.[{column}]"))})
                """
            : string.Empty;

        var sql = $"""
            MERGE [{schemaName}].[{tableName}] AS target
            USING
            (
                SELECT {string.Join(", ", columns.Select(column => $"@{column} AS [{column}]"))}
            ) AS source
            ON {onClause}
            WHEN MATCHED THEN
                UPDATE SET {string.Join(", ", updateColumns)}
            {insertClause}
            {(outputColumns.Count > 0 ? $"OUTPUT {string.Join(", ", outputColumns.Select(c => $"INSERTED.[{c}]"))}" : string.Empty)};
            """;

        await using var command = new SqlCommand(sql, connection);
        foreach (var column in columns)
        {
            command.Parameters.AddWithValue($"@{column}", ResolveColumnValue(record, column, context) ?? DBNull.Value);
        }

        if (outputColumns.Count == 0)
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
            return EmptyColumnValues;
        }

        return await ExecuteCapturingOutputAsync(command, outputColumns, cancellationToken);
    }

    /// <summary>The parent-key column(s) any of this record's child tables need captured off the parent
    /// write — via <c>OUTPUT INSERTED.[col]</c>, which works whether the column's value came from an
    /// explicit mapped field or was SQL Server-generated (IDENTITY), so no special-casing is needed here.</summary>
    private static List<string> ResolveOutputColumns(MappedDestinationRecord record)
    {
        if (record.ChildTables is not { Count: > 0 } childTables)
        {
            return [];
        }

        return childTables
            .Select(child => ValidateIdentifier(child.ParentKeyColumn))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task<IReadOnlyDictionary<string, object?>> ExecuteCapturingOutputAsync(
        SqlCommand command, IReadOnlyList<string> outputColumns, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return EmptyColumnValues;
        }

        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < outputColumns.Count; i++)
        {
            values[outputColumns[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        }

        return values;
    }

    private static async Task WriteChildTablesAsync(
        SqlConnection connection,
        MappedDestinationRecord record,
        IReadOnlyList<MappedChildTableRecord> childTables,
        IReadOnlyDictionary<string, object?> capturedParentColumns,
        bool deleteExistingChildRows,
        CancellationToken cancellationToken)
    {
        foreach (var childTable in childTables)
        {
            if (childTable.Rows.Count == 0)
            {
                continue;
            }

            // Defensive only: ConfiguredPipelineService.BuildChildTableRecords now leaves ForeignKeyColumn
            // blank instead of dropping the child table outright when no mapped field carries it (a schema-
            // less destination like Mongo can write such a table independently — see that method's doc
            // comment) — a relational table with no FK to link back to its parent can't be written
            // meaningfully here, so skip it. In practice this never fires for SQL: its mapping canvas only
            // ever creates a child table via the create-table modal, which always sets this.
            if (string.IsNullOrWhiteSpace(childTable.ForeignKeyColumn))
            {
                continue;
            }

            var parentKeyValue = capturedParentColumns.TryGetValue(childTable.ParentKeyColumn, out var captured)
                ? captured
                : record.Values.TryGetValue(childTable.ParentKeyColumn, out var mapped) ? mapped : null;

            if (parentKeyValue is null)
            {
                throw new InvalidOperationException(
                    $"Cannot write child table '{childTable.TableName}': parent key column " +
                    $"'{childTable.ParentKeyColumn}' had no value after the parent row was written.");
            }

            var (childSchema, childTableName) = ParseDestinationObject(childTable.TableName);

            if (deleteExistingChildRows)
            {
                await DeleteChildRowsAsync(connection, childSchema, childTableName, childTable.ForeignKeyColumn, parentKeyValue, cancellationToken);
            }

            await InsertChildRowsAsync(connection, childSchema, childTableName, childTable, parentKeyValue, cancellationToken);
        }
    }

    private static async Task DeleteChildRowsAsync(
        SqlConnection connection,
        string schemaName,
        string tableName,
        string foreignKeyColumn,
        object? parentKeyValue,
        CancellationToken cancellationToken)
    {
        var fk = ValidateIdentifier(foreignKeyColumn);
        var sql = $"DELETE FROM [{schemaName}].[{tableName}] WHERE [{fk}] = @ParentKey;";

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@ParentKey", parentKeyValue ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertChildRowsAsync(
        SqlConnection connection,
        string schemaName,
        string tableName,
        MappedChildTableRecord childTable,
        object? parentKeyValue,
        CancellationToken cancellationToken)
    {
        var fk = ValidateIdentifier(childTable.ForeignKeyColumn);

        foreach (var row in childTable.Rows)
        {
            // "RowIndex" is a synthetic key JsonMappingEngine adds internally to align SeparateDestination
            // rows — not a real mapped column, so it must never reach the INSERT.
            var fieldNames = row.Keys
                .Where(key => !string.Equals(key, "RowIndex", StringComparison.OrdinalIgnoreCase))
                .Select(ValidateIdentifier)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var columns = new[] { fk }.Concat(fieldNames).ToList();

            var sql = $"""
                INSERT INTO [{schemaName}].[{tableName}]
                (
                    {string.Join(", ", columns.Select(column => $"[{column}]"))}
                )
                VALUES
                (
                    {string.Join(", ", columns.Select(column => $"@{column}"))}
                );
                """;

            await using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue($"@{fk}", parentKeyValue ?? DBNull.Value);
            foreach (var fieldName in fieldNames)
            {
                command.Parameters.AddWithValue($"@{fieldName}", row[fieldName] ?? DBNull.Value);
            }

            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task EnsureCdcTableAsync(
        SqlConnection connection,
        string schemaName,
        string tableName,
        CancellationToken cancellationToken)
    {
        var cdcTableName = $"{tableName}_Cdc";
        // ddl-allowed: CDC companion table — open decision (retire vs. customer-provisioned table), see
        // docs/backend/11-destination-schema-ownership-plan.md section 3.A.3.
        var createTableSql = $"""
            IF OBJECT_ID(N'[{schemaName}].[{cdcTableName}]', N'U') IS NULL
            BEGIN
                CREATE TABLE [{schemaName}].[{cdcTableName}]
                (
                    FHIRBridgeCdcId BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_{schemaName}_{cdcTableName}_FHIRBridgeCdcId PRIMARY KEY,
                    PipelineRunId UNIQUEIDENTIFIER NOT NULL,
                    ResourceType NVARCHAR(100) NOT NULL,
                    SourceResourceId NVARCHAR(200) NULL,
                    Operation NVARCHAR(50) NOT NULL,
                    CapturedOnUtc DATETIME2 NOT NULL,
                    PayloadJson NVARCHAR(MAX) NOT NULL
                );
            END
            """;

        await using var command = new SqlCommand(createTableSql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertCdcRecordAsync(
        SqlConnection connection,
        string schemaName,
        string tableName,
        MappedDestinationRecord record,
        CancellationToken cancellationToken)
    {
        var cdcTableName = $"{tableName}_Cdc";
        var sql = $"""
            INSERT INTO [{schemaName}].[{cdcTableName}]
            (
                PipelineRunId,
                ResourceType,
                SourceResourceId,
                Operation,
                CapturedOnUtc,
                PayloadJson
            )
            VALUES
            (
                @PipelineRunId,
                @ResourceType,
                @SourceResourceId,
                @Operation,
                @CapturedOnUtc,
                @PayloadJson
            );
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@PipelineRunId", record.PipelineRunId);
        command.Parameters.AddWithValue("@ResourceType", record.ResourceType);
        command.Parameters.AddWithValue("@SourceResourceId", (object?)record.SourceResourceId ?? DBNull.Value);
        command.Parameters.AddWithValue("@Operation", "Upsert");
        command.Parameters.AddWithValue("@CapturedOnUtc", DateTime.UtcNow);
        command.Parameters.AddWithValue("@PayloadJson", JsonSerializer.Serialize(record.Values));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static object? ResolveColumnValue(MappedDestinationRecord record, string column, PipelineWriteContext context)
    {
        return column switch
        {
            "PipelineRunId" => record.PipelineRunId,
            "ResourceType" => record.ResourceType,
            "SourceResourceId" => record.SourceResourceId,
            "WrittenOnUtc" => context.RunStartedAtUtc.UtcDateTime,
            "LastUpdatedOnUtc" => context.RunStartedAtUtc.UtcDateTime,
            _ => record.Values.TryGetValue(column, out var mappedValue) ? mappedValue : null
        };
    }

    private static bool TryGetKeyValue(
        MappedDestinationRecord record,
        string keyColumn,
        out object? keyValue)
    {
        if (string.Equals(keyColumn, "SourceResourceId", StringComparison.OrdinalIgnoreCase))
        {
            keyValue = record.SourceResourceId;
            return !string.IsNullOrWhiteSpace(record.SourceResourceId);
        }

        return record.Values.TryGetValue(keyColumn, out keyValue) && keyValue is not null;
    }

    private static SqlDestinationTarget ParseDestinationTarget(string destinationObject, MappingProfile mappingProfile)
    {
        var objectAndOptions = destinationObject.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var objectName = objectAndOptions[0];
        var queryIndex = objectName.IndexOf('?', StringComparison.Ordinal);
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (queryIndex >= 0)
        {
            foreach (var option in objectName[(queryIndex + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                AddOption(options, option);
            }

            objectName = objectName[..queryIndex];
        }

        foreach (var option in objectAndOptions.Skip(1))
        {
            AddOption(options, option);
        }

        var (schemaName, tableName) = ParseDestinationObject(objectName);
        var writeMode = options.TryGetValue("mode", out var configuredMode)
            ? ParseWriteMode(configuredMode)
            : SqlDestinationWriteMode.Insert;

        // Prefer the structured IsUpsertKey flag; fall back to the legacy '?key=' option so destinations configured
        // before the mapping UI grows an IsUpsertKey control keep working; finally default to the system
        // SourceResourceId column every writer-created table has (see needsKeyFallback in WriteAsync for what
        // happens when even that doesn't exist on the actual table).
        var keyColumn = ResolveUpsertKeyColumn(mappingProfile)
            ?? (options.TryGetValue("key", out var configuredKey) ? ValidateIdentifier(configuredKey) : null)
            ?? "SourceResourceId";

        return new SqlDestinationTarget(schemaName, tableName, writeMode, keyColumn);
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

    private static (string SchemaName, string TableName) ParseDestinationObject(string destinationObject)
    {
        var parts = destinationObject.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return parts.Length switch
        {
            1 => ("dbo", ValidateIdentifier(parts[0])),
            2 => (ValidateIdentifier(parts[0]), ValidateIdentifier(parts[1])),
            _ => throw new InvalidOperationException("Destination object must be either TableName or SchemaName.TableName.")
        };
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

    private static SqlDestinationWriteMode ParseWriteMode(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "upsert" => SqlDestinationWriteMode.Upsert,
            "update" => SqlDestinationWriteMode.Update,
            "cdc" => SqlDestinationWriteMode.Cdc,
            _ => SqlDestinationWriteMode.Insert
        };
    }

    private static string ValidateIdentifier(string identifier) => SqlIdentifier.Validate(identifier);

    private sealed record SqlDestinationTarget(
        string SchemaName,
        string TableName,
        SqlDestinationWriteMode WriteMode,
        string KeyColumn);

    private enum SqlDestinationWriteMode
    {
        Insert,
        Upsert,

        /// <summary>Updates the matching row by key only; never inserts. A record whose key has no match in the
        /// table is left unwritten.</summary>
        Update,

        /// <summary>
        /// Application-level change history: the row is inserted into the target table and also appended to a
        /// companion <c>{Table}_Cdc</c> table. This is not SQL Server's native Change Data Capture.
        /// </summary>
        Cdc
    }
}
