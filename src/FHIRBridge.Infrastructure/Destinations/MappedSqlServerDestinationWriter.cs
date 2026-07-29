using System.Text.RegularExpressions;
using System.Text.Json;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Integration.Sql;
using Microsoft.Data.SqlClient;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Writes mapped records to SQL Server / Azure SQL. The customer owns the destination schema: the target table must
/// already exist, and only the mapped destination columns are ever written — no system/audit columns are added.
/// Supports Insert, Upsert (MERGE on the mapped field flagged <see cref="MappingField.IsUpsertKey"/>), Update
/// (updates the matching row by that same key only, never inserts), and CDC write modes.
/// Note: "CDC" here is an application-level change-history approximation — each write is mirrored into a
/// companion <c>{Table}_Cdc</c> table — and is NOT SQL Server's native Change Data Capture feature.
/// TODO: CDC mode still auto-creates its companion table, which conflicts with the customer-owned-schema model;
/// its fate (retire vs. require a customer-provisioned table) is an open decision (see
/// docs/backend/11-destination-schema-ownership-plan.md section 3.A.3).
/// </summary>
public sealed partial class MappedSqlServerDestinationWriter : IConfiguredDestinationWriter
{
    private readonly ISecretProvider _secretProvider;

    public MappedSqlServerDestinationWriter(ISecretProvider secretProvider)
    {
        _secretProvider = secretProvider;
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

        // FHIRBridge's own output tables carry NOT NULL audit/lineage columns (PipelineRunId, ResourceType,
        // WrittenOnUtc, ...) that describe the run rather than the source resource, so they are never part of a
        // field mapping. Discover which of them this target table actually declares and fill them from the run
        // context per record — a customer-owned table without those columns gets nothing extra.
        var systemColumns = await ReadSystemColumnsPresentAsync(
            connection, target.SchemaName, target.TableName, cancellationToken);

        // Each record's write stands alone: a constraint violation (dup key, NOT NULL, truncation, conversion, ...)
        // on one record's data must not discard every other record already validated and ready to write in the same
        // batch. Only a SqlException is caught here — anything else (e.g. the connection itself dying) can't be
        // recovered from per-record and is left to propagate and fail the whole route, as before.
        var recordErrors = new List<string>();
        var writtenResourceIds = new List<string?>();

        foreach (var record in records)
        {
            var toWrite = AugmentWithSystemColumns(record, systemColumns, context);
            try
            {
                switch (target.WriteMode)
                {
                    case SqlDestinationWriteMode.Upsert:
                        await UpsertRecordAsync(connection, target.SchemaName, target.TableName, toWrite, target.KeyColumn!, cancellationToken);
                        break;
                    case SqlDestinationWriteMode.Update:
                        await UpdateRecordAsync(connection, target.SchemaName, target.TableName, toWrite, target.KeyColumn!, cancellationToken);
                        break;
                    case SqlDestinationWriteMode.Cdc:
                        await InsertRecordAsync(connection, target.SchemaName, target.TableName, toWrite, cancellationToken);
                        await InsertCdcRecordAsync(connection, target.SchemaName, target.TableName, toWrite, cancellationToken);
                        break;
                    default:
                        await InsertRecordAsync(connection, target.SchemaName, target.TableName, toWrite, cancellationToken);
                        break;
                }

                writtenResourceIds.Add(record.SourceResourceId);
            }
            catch (SqlException exception)
            {
                recordErrors.Add(
                    $"{record.ResourceType}/{record.SourceResourceId ?? "unknown"}: {exception.Message}");
            }
        }

        return new DestinationWriteResult(
            writtenResourceIds.Count,
            RecordErrors: recordErrors.Count > 0 ? recordErrors : null,
            WrittenResourceIds: writtenResourceIds);
    }

    // FHIRBridge-managed audit/lineage columns and how to fill each from the run: these describe the pipeline run,
    // not the source resource, so they are auto-populated (never mapped). Only columns the target table actually
    // declares are written; a mapping that explicitly sets one of these wins over the auto value (see
    // AugmentWithSystemColumns), so this never overrides a customer's own intent.
    private static readonly IReadOnlyDictionary<string, Func<MappedDestinationRecord, PipelineWriteContext, object?>> SystemColumnValues =
        new Dictionary<string, Func<MappedDestinationRecord, PipelineWriteContext, object?>>(StringComparer.OrdinalIgnoreCase)
        {
            ["PipelineRunId"] = (record, _) => record.PipelineRunId,
            ["ResourceType"] = (record, _) => record.ResourceType,
            ["WrittenOnUtc"] = (_, context) => context.RunStartedAtUtc.UtcDateTime,
            ["LastUpdatedOnUtc"] = (_, context) => context.RunStartedAtUtc.UtcDateTime,
        };

    /// <summary>The FHIRBridge-managed system columns (see <see cref="SystemColumnValues"/>) that the target table
    /// actually declares — the only ones safe to auto-populate. Empty for a customer-owned table without them.</summary>
    private static async Task<IReadOnlyList<string>> ReadSystemColumnsPresentAsync(
        SqlConnection connection,
        string schemaName,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT COLUMN_NAME
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = @Schema AND TABLE_NAME = @Table
            """,
            connection);
        command.Parameters.AddWithValue("@Schema", schemaName);
        command.Parameters.AddWithValue("@Table", tableName);

        var present = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var column = reader.GetString(0);
            if (SystemColumnValues.ContainsKey(column))
            {
                present.Add(column);
            }
        }

        return present;
    }

    /// <summary>Returns the record with any target-declared system column filled from the run context, unless the
    /// mapping already provided that column (an explicit mapping always wins). Returns the record unchanged when the
    /// table declares none of them.</summary>
    private static MappedDestinationRecord AugmentWithSystemColumns(
        MappedDestinationRecord record,
        IReadOnlyList<string> systemColumns,
        PipelineWriteContext context)
    {
        if (systemColumns.Count == 0)
        {
            return record;
        }

        var values = new Dictionary<string, object?>(record.Values, StringComparer.OrdinalIgnoreCase);
        var added = false;
        foreach (var column in systemColumns)
        {
            if (values.ContainsKey(column))
            {
                continue;
            }

            values[column] = SystemColumnValues[column](record, context);
            added = true;
        }

        return added ? record with { Values = values } : record;
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
                $"Destination table '{schemaName}.{tableName}' does not exist. Create it in your database before running this pipeline.");
        }
    }

    private static async Task InsertRecordAsync(
        SqlConnection connection,
        string schemaName,
        string tableName,
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

        var parameterNames = columns.Select(column => $"@{column}").ToList();

        var sql = $"""
            INSERT INTO [{schemaName}].[{tableName}]
            (
                {string.Join(", ", columns.Select(column => $"[{column}]"))}
            )
            VALUES
            (
                {string.Join(", ", parameterNames)}
            );
            """;

        await using var command = new SqlCommand(sql, connection);
        foreach (var column in columns)
        {
            command.Parameters.AddWithValue($"@{column}", record.Values[column] ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertRecordAsync(
        SqlConnection connection,
        string schemaName,
        string tableName,
        MappedDestinationRecord record,
        string keyColumn,
        CancellationToken cancellationToken)
    {
        if (!TryGetKeyValue(record, keyColumn, out var keyValue))
        {
            await InsertRecordAsync(connection, schemaName, tableName, record, cancellationToken);
            return;
        }

        var columns = record.Values.Keys
            .Select(ValidateIdentifier)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var parameterNames = columns.Select(column => $"@{column}").ToList();
        var updateColumns = columns
            .Where(column => !string.Equals(column, keyColumn, StringComparison.OrdinalIgnoreCase))
            .Select(column => $"target.[{column}] = source.[{column}]")
            .ToList();

        // A mapping profile with only the upsert-key field configured (no other columns mapped) leaves
        // updateColumns empty — "UPDATE SET" with nothing after it is invalid T-SQL. There is nothing meaningful
        // to update in that case anyway, so omit the WHEN MATCHED clause entirely: MERGE still inserts a row the
        // first time a given key is seen and is a no-op on every subsequent match, which is exactly the intended
        // upsert behavior when the key is the only mapped field.
        var matchedClause = updateColumns.Count > 0
            ? $"""
              WHEN MATCHED THEN
                  UPDATE SET {string.Join(", ", updateColumns)}
              """
            : string.Empty;

        var sql = $"""
            MERGE [{schemaName}].[{tableName}] AS target
            USING
            (
                SELECT {string.Join(", ", parameterNames.Select((parameter, index) => $"{parameter} AS [{columns[index]}]"))}
            ) AS source
            ON target.[{ValidateIdentifier(keyColumn)}] = source.[{ValidateIdentifier(keyColumn)}]
            {matchedClause}
            WHEN NOT MATCHED THEN
                INSERT ({string.Join(", ", columns.Select(column => $"[{column}]"))})
                VALUES ({string.Join(", ", columns.Select(column => $"source.[{column}]"))});
            """;

        await using var command = new SqlCommand(sql, connection);
        AddRecordParameters(command, record, columns, keyColumn, keyValue);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Updates the matching row by <paramref name="keyColumn"/> only — never inserts. A record whose key
    /// isn't present in the table (or whose key value is missing) is simply left unwritten, which is exactly what
    /// "Update only" means.</summary>
    private static async Task UpdateRecordAsync(
        SqlConnection connection,
        string schemaName,
        string tableName,
        MappedDestinationRecord record,
        string keyColumn,
        CancellationToken cancellationToken)
    {
        if (!TryGetKeyValue(record, keyColumn, out var keyValue))
        {
            return;
        }

        var columns = record.Values.Keys
            .Select(ValidateIdentifier)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(column => !string.Equals(column, keyColumn, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (columns.Count == 0)
        {
            return;
        }

        var setClause = string.Join(", ", columns.Select(column => $"[{column}] = @{column}"));
        var sql = $"""
            UPDATE [{schemaName}].[{tableName}]
            SET {setClause}
            WHERE [{ValidateIdentifier(keyColumn)}] = @{keyColumn};
            """;

        await using var command = new SqlCommand(sql, connection);
        AddRecordParameters(command, record, columns, keyColumn, keyValue);
        command.Parameters.AddWithValue($"@{keyColumn}", keyValue ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
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

    private static void AddRecordParameters(
        SqlCommand command,
        MappedDestinationRecord record,
        IReadOnlyCollection<string> columns,
        string keyColumn,
        object? keyValue)
    {
        foreach (var column in columns)
        {
            var value = string.Equals(column, keyColumn, StringComparison.OrdinalIgnoreCase)
                ? keyValue
                : record.Values.TryGetValue(column, out var mappedValue) ? mappedValue : null;

            command.Parameters.AddWithValue($"@{column}", value ?? DBNull.Value);
        }
    }

    private static bool TryGetKeyValue(
        MappedDestinationRecord record,
        string keyColumn,
        out object? keyValue)
        => record.Values.TryGetValue(keyColumn, out keyValue) && keyValue is not null;

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
        // before the mapping UI grows an IsUpsertKey control (docs/backend/11-destination-schema-ownership-plan.md
        // section 4 item 5) keep working. Remove the fallback once that UI work lands.
        var keyColumn = ResolveUpsertKeyColumn(mappingProfile)
            ?? (options.TryGetValue("key", out var configuredKey) ? ValidateIdentifier(configuredKey) : null);

        if (writeMode == SqlDestinationWriteMode.Upsert && keyColumn is null)
        {
            throw new InvalidOperationException(
                $"Destination '{schemaName}.{tableName}' is configured for Upsert mode but no mapped field is " +
                "designated as the upsert key. Mark one mapped field's IsUpsertKey in the mapping profile.");
        }

        if (writeMode == SqlDestinationWriteMode.Update && keyColumn is null)
        {
            throw new InvalidOperationException(
                $"Destination '{schemaName}.{tableName}' is configured for Update mode but no mapped field is " +
                "designated as the update key. Mark one mapped field's IsUpsertKey in the mapping profile.");
        }

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
            "cdc" => SqlDestinationWriteMode.Cdc,
            "update" => SqlDestinationWriteMode.Update,
            _ => SqlDestinationWriteMode.Insert
        };
    }

    private static string ValidateIdentifier(string identifier)
    {
        if (!SqlIdentifierRegex().IsMatch(identifier))
        {
            throw new InvalidOperationException($"'{identifier}' is not a valid SQL identifier.");
        }

        return identifier;
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex SqlIdentifierRegex();

    private sealed record SqlDestinationTarget(
        string SchemaName,
        string TableName,
        SqlDestinationWriteMode WriteMode,
        string? KeyColumn);

    private enum SqlDestinationWriteMode
    {
        Insert,
        Upsert,

        /// <summary>
        /// Application-level change history: the row is inserted into the target table and also appended to a
        /// companion <c>{Table}_Cdc</c> table. This is not SQL Server's native Change Data Capture.
        /// </summary>
        Cdc,

        /// <summary>Updates the matching row by key only; never inserts. A record whose key has no match in the
        /// table is left unwritten.</summary>
        Update
    }
}
