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
    // System-managed columns the writer always emits. A mapping field targeting one of these is ignored (the
    // system value wins) so the generated CREATE TABLE / INSERT never declares a column twice.
    private static readonly HashSet<string> ReservedColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "FHIRBridgeRowId", "PipelineRunId", "ResourceType", "SourceResourceId", "WrittenOnUtc", "LastUpdatedOnUtc"
    };

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

        foreach (var record in records)
        {
            switch (target.WriteMode)
            {
                case SqlDestinationWriteMode.Upsert:
                    await UpsertRecordAsync(connection, target.SchemaName, target.TableName, record, target.KeyColumn, cancellationToken);
                    break;
                case SqlDestinationWriteMode.Cdc:
                    await InsertRecordAsync(connection, target.SchemaName, target.TableName, record, cancellationToken);
                    await InsertCdcRecordAsync(connection, target.SchemaName, target.TableName, record, cancellationToken);
                    break;
                default:
                    await InsertRecordAsync(connection, target.SchemaName, target.TableName, record, cancellationToken);
                    break;
            }
        }

        return new DestinationWriteResult(records.Count);
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

    private static async Task InsertRecordAsync(
        SqlConnection connection,
        string schemaName,
        string tableName,
        MappedDestinationRecord record,
        CancellationToken cancellationToken)
    {
        var fieldNames = record.Values.Keys
            .Where(key => !ReservedColumns.Contains(key))
            .Select(ValidateIdentifier)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var columns = new[]
        {
            "PipelineRunId",
            "ResourceType",
            "SourceResourceId",
            "WrittenOnUtc"
        }.Concat(fieldNames).ToList();
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
        command.Parameters.AddWithValue("@PipelineRunId", record.PipelineRunId);
        command.Parameters.AddWithValue("@ResourceType", record.ResourceType);
        command.Parameters.AddWithValue("@SourceResourceId", (object?)record.SourceResourceId ?? DBNull.Value);
        command.Parameters.AddWithValue("@WrittenOnUtc", DateTime.UtcNow);

        foreach (var targetField in fieldNames)
        {
            command.Parameters.AddWithValue($"@{targetField}", record.Values[targetField] ?? DBNull.Value);
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

        var fieldNames = record.Values.Keys
            .Where(key => !ReservedColumns.Contains(key))
            .Select(ValidateIdentifier)
            .ToList();
        var standardColumns = new[]
        {
            "PipelineRunId",
            "ResourceType",
            "SourceResourceId",
            "WrittenOnUtc",
            "LastUpdatedOnUtc"
        };
        var columns = standardColumns.Concat(fieldNames).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var parameterNames = columns.Select(column => $"@{column}").ToList();
        var updateColumns = columns
            .Where(column => !string.Equals(column, "WrittenOnUtc", StringComparison.OrdinalIgnoreCase))
            .Where(column => !string.Equals(column, keyColumn, StringComparison.OrdinalIgnoreCase))
            .Select(column => $"target.[{column}] = source.[{column}]");

        var sql = $"""
            MERGE [{schemaName}].[{tableName}] AS target
            USING
            (
                SELECT {string.Join(", ", parameterNames.Select((parameter, index) => $"{parameter} AS [{columns[index]}]"))}
            ) AS source
            ON target.[ResourceType] = source.[ResourceType]
               AND target.[{ValidateIdentifier(keyColumn)}] = source.[{ValidateIdentifier(keyColumn)}]
            WHEN MATCHED THEN
                UPDATE SET {string.Join(", ", updateColumns)}
            WHEN NOT MATCHED THEN
                INSERT ({string.Join(", ", columns.Select(column => $"[{column}]"))})
                VALUES ({string.Join(", ", columns.Select(column => $"source.[{column}]"))});
            """;

        await using var command = new SqlCommand(sql, connection);
        AddRecordParameters(command, record, columns, keyColumn, keyValue);
        await command.ExecuteNonQueryAsync(cancellationToken);
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

    private static void AddRecordParameters(
        SqlCommand command,
        MappedDestinationRecord record,
        IReadOnlyCollection<string> columns,
        string keyColumn,
        object? keyValue)
    {
        foreach (var column in columns)
        {
            var value = column switch
            {
                "PipelineRunId" => record.PipelineRunId,
                "ResourceType" => record.ResourceType,
                "SourceResourceId" => record.SourceResourceId,
                "WrittenOnUtc" => DateTime.UtcNow,
                "LastUpdatedOnUtc" => DateTime.UtcNow,
                _ when string.Equals(column, keyColumn, StringComparison.OrdinalIgnoreCase) => keyValue,
                _ => record.Values.TryGetValue(column, out var mappedValue) ? mappedValue : null
            };

            command.Parameters.AddWithValue($"@{column}", value ?? DBNull.Value);
        }
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
