using System.Text.Json;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Integration.Sql;
using Microsoft.Data.SqlClient;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Writes mapped records to SQL Server / Azure SQL, auto-creating the target schema and table.
/// Supports Insert, Upsert (MERGE on resource type + key column), and CDC write modes.
/// Note: "CDC" here is an application-level change-history approximation — each write is mirrored into a
/// companion <c>{Table}_Cdc</c> table — and is NOT SQL Server's native Change Data Capture feature.
/// </summary>
public sealed class MappedSqlServerDestinationWriter : IConfiguredDestinationWriter
{
    // System-managed columns the writer emits when they exist on the target table. A mapping field targeting
    // one of these is ignored (the system value wins) so the generated CREATE TABLE / INSERT never declares a
    // column twice. Tables created by the Mapping Config Import feature don't have these columns at all — the
    // writer detects that per-table (see GetExistingColumnNamesAsync) and simply omits whichever are absent,
    // rather than assuming every target table was created by this writer's own EnsureTableAsync.
    private static readonly HashSet<string> ReservedColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "FHIRBridgeRowId", "PipelineRunId", "ResourceType", "SourceResourceId", "WrittenOnUtc", "LastUpdatedOnUtc"
    };

    private static readonly string[] InsertSystemColumns = ["PipelineRunId", "ResourceType", "SourceResourceId", "WrittenOnUtc"];
    private static readonly string[] UpsertSystemColumns = ["PipelineRunId", "ResourceType", "SourceResourceId", "WrittenOnUtc", "LastUpdatedOnUtc"];
    private static readonly IReadOnlyDictionary<string, object?> EmptyColumnValues = new Dictionary<string, object?>();

    private readonly ISecretProvider _secretProvider;

    public MappedSqlServerDestinationWriter(ISecretProvider secretProvider)
    {
        _secretProvider = secretProvider;
    }

    public async Task<int> WriteAsync(
        DestinationConfiguration destination,
        MappingProfile mappingProfile,
        IReadOnlyCollection<MappedDestinationRecord> records,
        CancellationToken cancellationToken)
    {
        if (records.Count == 0)
        {
            return 0;
        }

        var connectionString = await _secretProvider.GetSecretAsync(
            destination.SecretReference,
            cancellationToken);
        var target = ParseDestinationTarget(
            destination.Target ?? mappingProfile.DestinationObject);

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

        foreach (var record in records)
        {
            IReadOnlyDictionary<string, object?> capturedParentColumns;
            switch (target.WriteMode)
            {
                case SqlDestinationWriteMode.Upsert:
                    capturedParentColumns = await UpsertRecordAsync(
                        connection, target.SchemaName, target.TableName, record, target.KeyColumn, existingColumns, cancellationToken);
                    break;
                case SqlDestinationWriteMode.Cdc:
                    capturedParentColumns = await InsertRecordAsync(
                        connection, target.SchemaName, target.TableName, record, existingColumns, cancellationToken);
                    await InsertCdcRecordAsync(connection, target.SchemaName, target.TableName, record, cancellationToken);
                    break;
                default:
                    capturedParentColumns = await InsertRecordAsync(
                        connection, target.SchemaName, target.TableName, record, existingColumns, cancellationToken);
                    break;
            }

            if (record.ChildTables is { Count: > 0 } childTables)
            {
                await WriteChildTablesAsync(
                    connection, record, childTables, capturedParentColumns,
                    deleteExistingChildRows: target.WriteMode == SqlDestinationWriteMode.Upsert,
                    cancellationToken);
            }
        }

        return records.Count;
    }

    private static async Task EnsureTableAsync(
        SqlConnection connection,
        string schemaName,
        string tableName,
        MappingProfile mappingProfile,
        CancellationToken cancellationToken)
    {
        var createSchemaSql = $"""
            IF SCHEMA_ID(N'{schemaName}') IS NULL
            BEGIN
                EXEC(N'CREATE SCHEMA [{schemaName}]')
            END
            """;

        await using (var command = new SqlCommand(createSchemaSql, connection))
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var mappedColumns = mappingProfile.Fields
            .Where(field =>
                string.IsNullOrWhiteSpace(field.ResourceType) ||
                string.Equals(field.ResourceType, mappingProfile.ResourceType, StringComparison.OrdinalIgnoreCase))
            .Where(field =>
                string.IsNullOrWhiteSpace(field.DestinationObject) ||
                string.Equals(field.DestinationObject, mappingProfile.DestinationObject, StringComparison.OrdinalIgnoreCase))
            .Where(field => !ReservedColumns.Contains(field.TargetField))
            .GroupBy(field => field.TargetField, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select(field => $"[{ValidateIdentifier(field.TargetField)}] {GetSqlType(field.ValueType)} NULL");
        var createTableSql = $"""
            IF OBJECT_ID(N'[{schemaName}].[{tableName}]', N'U') IS NULL
            BEGIN
                CREATE TABLE [{schemaName}].[{tableName}]
                (
                    FHIRBridgeRowId BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_{schemaName}_{tableName}_FHIRBridgeRowId PRIMARY KEY,
                    PipelineRunId UNIQUEIDENTIFIER NOT NULL,
                    ResourceType NVARCHAR(100) NOT NULL,
                    SourceResourceId NVARCHAR(200) NULL,
                    WrittenOnUtc DATETIME2 NOT NULL,
                    LastUpdatedOnUtc DATETIME2 NULL,
                    {string.Join(",\n                    ", mappedColumns)}
                );
            END
            """;

        await using (var command = new SqlCommand(createTableSql, connection))
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Reads the real column set of the target table. Used to tolerate tables the Mapping Config Import
    /// feature created directly (a PK plus mapped columns only, no reserved system columns) instead of
    /// assuming every table this writer touches was created by its own <see cref="EnsureTableAsync"/>.
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

    private static async Task<IReadOnlyDictionary<string, object?>> InsertRecordAsync(
        SqlConnection connection,
        string schemaName,
        string tableName,
        MappedDestinationRecord record,
        IReadOnlySet<string> existingColumns,
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
            command.Parameters.AddWithValue($"@{column}", ResolveColumnValue(record, column) ?? DBNull.Value);
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
        CancellationToken cancellationToken)
    {
        if (!TryGetKeyValue(record, keyColumn, out _))
        {
            return await InsertRecordAsync(connection, schemaName, tableName, record, existingColumns, cancellationToken);
        }

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

        var sql = $"""
            MERGE [{schemaName}].[{tableName}] AS target
            USING
            (
                SELECT {string.Join(", ", columns.Select(column => $"@{column} AS [{column}]"))}
            ) AS source
            ON {onClause}
            WHEN MATCHED THEN
                UPDATE SET {string.Join(", ", updateColumns)}
            WHEN NOT MATCHED THEN
                INSERT ({string.Join(", ", columns.Select(column => $"[{column}]"))})
                VALUES ({string.Join(", ", columns.Select(column => $"source.[{column}]"))})
            {(outputColumns.Count > 0 ? $"OUTPUT {string.Join(", ", outputColumns.Select(c => $"INSERTED.[{c}]"))}" : string.Empty)};
            """;

        await using var command = new SqlCommand(sql, connection);
        foreach (var column in columns)
        {
            command.Parameters.AddWithValue($"@{column}", ResolveColumnValue(record, column) ?? DBNull.Value);
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
        var captured = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            for (var i = 0; i < outputColumns.Count; i++)
            {
                captured[outputColumns[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
        }

        return captured;
    }

    /// <summary>
    /// Writes every child table attached to a parent record, once the parent row itself has been written.
    /// Insert mode always appends child rows (matching the parent's own append-only behavior). Upsert mode
    /// deletes any child rows already linked to this parent key before inserting the fresh set — child rows
    /// have no dedup key of their own, so without this a re-run of the same resource would duplicate them
    /// indefinitely even though the parent row itself is correctly upserted in place.
    /// </summary>
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

    private static object? ResolveColumnValue(MappedDestinationRecord record, string column)
    {
        return column switch
        {
            "PipelineRunId" => record.PipelineRunId,
            "ResourceType" => record.ResourceType,
            "SourceResourceId" => record.SourceResourceId,
            "WrittenOnUtc" => DateTime.UtcNow,
            "LastUpdatedOnUtc" => DateTime.UtcNow,
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

    private static SqlDestinationTarget ParseDestinationTarget(string destinationObject)
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
        var keyColumn = options.TryGetValue("key", out var configuredKey)
            ? ValidateIdentifier(configuredKey)
            : "SourceResourceId";

        return new SqlDestinationTarget(schemaName, tableName, writeMode, keyColumn);
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
            _ => SqlDestinationWriteMode.Insert
        };
    }

    private static string ValidateIdentifier(string identifier) => SqlIdentifier.Validate(identifier);

    private static string GetSqlType(MappingValueType valueType)
    {
        return valueType switch
        {
            MappingValueType.Integer => "INT",
            MappingValueType.Decimal => "DECIMAL(18, 4)",
            MappingValueType.Boolean => "BIT",
            MappingValueType.Date => "DATE",
            MappingValueType.DateTime => "DATETIME2",
            MappingValueType.Json => "NVARCHAR(MAX)",
            _ => "NVARCHAR(MAX)"
        };
    }

    private sealed record SqlDestinationTarget(
        string SchemaName,
        string TableName,
        SqlDestinationWriteMode WriteMode,
        string KeyColumn);

    private enum SqlDestinationWriteMode
    {
        Insert,
        Upsert,

        /// <summary>
        /// Application-level change history: the row is inserted into the target table and also appended to a
        /// companion <c>{Table}_Cdc</c> table. This is not SQL Server's native Change Data Capture.
        /// </summary>
        Cdc
    }
}
