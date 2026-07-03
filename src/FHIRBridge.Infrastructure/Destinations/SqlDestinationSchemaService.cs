using System.Data.Common;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Integration.Sql;
using FHIRBridge.SharedKernel.Exceptions;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Reads the live table/column schema for a relational destination so the mapping UI can offer real column pickers.
/// Provider-aware: SQL Server / Azure SQL, PostgreSQL, and MySQL are introspected via <c>information_schema.columns</c>.
/// Non-relational destinations return an empty schema (the UI falls back to FHIR default tables).
/// </summary>
public sealed class SqlDestinationSchemaService : IDestinationSchemaService
{
    private const string InformationSchemaSql = """
        SELECT table_schema, table_name, column_name, data_type, is_nullable, character_maximum_length
        FROM information_schema.columns
        WHERE table_schema NOT IN ('pg_catalog', 'information_schema', 'mysql', 'performance_schema', 'sys')
        ORDER BY table_schema, table_name, ordinal_position
        """;

    private readonly IConfigurationRepository _repository;
    private readonly ISecretProvider _secretProvider;

    public SqlDestinationSchemaService(
        IConfigurationRepository repository,
        ISecretProvider secretProvider)
    {
        _repository = repository;
        _secretProvider = secretProvider;
    }

    public async Task<DestinationSchemaDto> GetSchemaAsync(
        Guid destinationId,
        CancellationToken cancellationToken)
    {
        var destination = await _repository.GetDestinationAsync(destinationId, cancellationToken)
            ?? throw new NotFoundException("DestinationConfiguration", destinationId);

        if (!IsRelational(destination.DestinationType))
        {
            return new DestinationSchemaDto(destinationId, []);
        }

        var connectionString = await _secretProvider.GetSecretAsync(destination.SecretReference, cancellationToken);

        await using var connection = await OpenConnectionAsync(destination.DestinationType, connectionString, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = destination.DestinationType is DestinationType.SqlServer or DestinationType.AzureSql
            ? SqlServerColumnsSql
            : InformationSchemaSql;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var tables = await ReadColumnsAsync(reader, MapType(destination.DestinationType), cancellationToken);

        return new DestinationSchemaDto(destinationId, tables);
    }

    private static bool IsRelational(DestinationType type)
        => type is DestinationType.SqlServer or DestinationType.AzureSql or DestinationType.PostgreSql or DestinationType.MySql;

    private static async Task<DbConnection> OpenConnectionAsync(
        DestinationType type,
        string connectionString,
        CancellationToken cancellationToken)
    {
        switch (type)
        {
            case DestinationType.SqlServer:
            case DestinationType.AzureSql:
                return await SqlServerConnectionFactory.OpenConnectionAsync(connectionString, cancellationToken);
            case DestinationType.PostgreSql:
                var npgsql = new NpgsqlConnection(connectionString);
                await npgsql.OpenAsync(cancellationToken);
                return npgsql;
            case DestinationType.MySql:
                var mysql = new MySqlConnection(connectionString);
                await mysql.OpenAsync(cancellationToken);
                return mysql;
            default:
                throw new InvalidOperationException($"Destination type '{type}' is not relational.");
        }
    }

    private const string SqlServerColumnsSql = """
        SELECT TABLE_SCHEMA, TABLE_NAME, COLUMN_NAME, DATA_TYPE, IS_NULLABLE, CHARACTER_MAXIMUM_LENGTH
        FROM INFORMATION_SCHEMA.COLUMNS
        WHERE TABLE_SCHEMA NOT IN ('sys', 'INFORMATION_SCHEMA')
        ORDER BY TABLE_SCHEMA, TABLE_NAME, ORDINAL_POSITION;
        """;

    private static async Task<List<DestinationTableSchemaDto>> ReadColumnsAsync(
        DbDataReader reader,
        Func<string, string> mapType,
        CancellationToken cancellationToken)
    {
        var tables = new Dictionary<string, List<DestinationColumnSchemaDto>>(StringComparer.OrdinalIgnoreCase);
        var tableNames = new Dictionary<string, (string SchemaName, string TableName)>(StringComparer.OrdinalIgnoreCase);

        while (await reader.ReadAsync(cancellationToken))
        {
            var schemaName = reader.GetString(0);
            var tableName = reader.GetString(1);
            var fullName = $"{schemaName}.{tableName}";
            var dataType = reader.GetString(3);

            // character_maximum_length can exceed int range (e.g. MySQL LONGTEXT) — clamp oversized values to null.
            int? maxLength = null;
            if (!reader.IsDBNull(5))
            {
                var raw = Convert.ToInt64(reader.GetValue(5));
                maxLength = raw is > 0 and <= int.MaxValue ? (int)raw : null;
            }

            if (!tables.TryGetValue(fullName, out var columns))
            {
                columns = [];
                tables[fullName] = columns;
                tableNames[fullName] = (schemaName, tableName);
            }

            columns.Add(new DestinationColumnSchemaDto(
                reader.GetString(2),
                dataType,
                mapType(dataType),
                string.Equals(reader.GetString(4), "YES", StringComparison.OrdinalIgnoreCase),
                maxLength));
        }

        return tables
            .Select(pair =>
            {
                var (schemaName, tableName) = tableNames[pair.Key];
                return new DestinationTableSchemaDto(schemaName, tableName, pair.Key, pair.Value);
            })
            .ToList();
    }

    private static Func<string, string> MapType(DestinationType type)
        => type switch
        {
            DestinationType.PostgreSql => MapPostgresType,
            DestinationType.MySql => MapMySqlType,
            _ => MapSqlServerType
        };

    private static string MapSqlServerType(string dataType) => dataType.Trim().ToLowerInvariant() switch
    {
        "bit" => "Boolean",
        "tinyint" or "smallint" or "int" or "bigint" => "Integer",
        "decimal" or "numeric" or "money" or "smallmoney" or "float" or "real" => "Decimal",
        "date" => "Date",
        "datetime" or "datetime2" or "datetimeoffset" or "smalldatetime" => "DateTime",
        _ => "String"
    };

    private static string MapPostgresType(string dataType) => dataType.Trim().ToLowerInvariant() switch
    {
        "boolean" => "Boolean",
        "smallint" or "integer" or "bigint" => "Integer",
        "numeric" or "decimal" or "real" or "double precision" or "money" => "Decimal",
        "date" => "Date",
        "timestamp without time zone" or "timestamp with time zone" => "DateTime",
        _ => "String"
    };

    private static string MapMySqlType(string dataType) => dataType.Trim().ToLowerInvariant() switch
    {
        "tinyint" => "Boolean",
        "smallint" or "mediumint" or "int" or "integer" or "bigint" => "Integer",
        "decimal" or "numeric" or "float" or "double" => "Decimal",
        "date" => "Date",
        "datetime" or "timestamp" => "DateTime",
        _ => "String"
    };
}
