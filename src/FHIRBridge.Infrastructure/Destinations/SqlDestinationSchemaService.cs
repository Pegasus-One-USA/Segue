using System.Data.Common;
using FHIRBridge.Application.Abstractions.Audit;
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
/// Reads the live table/column schema for a relational destination so the mapping UI can offer real column pickers,
/// and executes additive DDL (ALTER TABLE ADD COLUMN / CREATE TABLE) for the mapping canvas's schema-authoring
/// affordances. Schema reads are provider-aware (SQL Server / Azure SQL, PostgreSQL, MySQL); DDL execution is
/// SQL Server / Azure SQL only. Non-relational destinations return an empty schema (the UI falls back to FHIR
/// default tables).
/// </summary>
public sealed class SqlDestinationSchemaService : IDestinationSchemaService
{
    private const string ModuleDestinationSchema = "DestinationSchema";

    private const string InformationSchemaSql = """
        SELECT table_schema, table_name, column_name, data_type, is_nullable, character_maximum_length
        FROM information_schema.columns
        WHERE table_schema NOT IN ('pg_catalog', 'information_schema', 'mysql', 'performance_schema', 'sys')
        ORDER BY table_schema, table_name, ordinal_position
        """;

    private readonly IConfigurationRepository _repository;
    private readonly ISecretProvider _secretProvider;
    private readonly IUserActivityAuditService _userActivityAuditService;
    private readonly ICurrentUserService _currentUserService;

    public SqlDestinationSchemaService(
        IConfigurationRepository repository,
        ISecretProvider secretProvider,
        IUserActivityAuditService userActivityAuditService,
        ICurrentUserService currentUserService)
    {
        _repository = repository;
        _secretProvider = secretProvider;
        _userActivityAuditService = userActivityAuditService;
        _currentUserService = currentUserService;
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
        var tables = await ReadSchemaAsync(destination.DestinationType, connectionString, cancellationToken);

        return new DestinationSchemaDto(destinationId, tables);
    }

    public async Task<DestinationSchemaProbeDto> ProbeSchemaAsync(
        DestinationConnectionProbeRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsRelational(request.DestinationType))
        {
            return new DestinationSchemaProbeDto(
                false, $"Destination type '{request.DestinationType}' is not a relational database.", []);
        }

        string connectionString;
        try
        {
            connectionString = BuildConnectionString(request);
        }
        catch (Exception exception)
        {
            return new DestinationSchemaProbeDto(false, exception.Message, []);
        }

        try
        {
            var tables = await ReadSchemaAsync(request.DestinationType, connectionString, cancellationToken);
            return new DestinationSchemaProbeDto(true, null, tables);
        }
        catch (Exception exception)
        {
            // Connection/auth failures are an expected UI outcome, not a server error.
            return new DestinationSchemaProbeDto(false, exception.Message, []);
        }
    }

    public async Task<SchemaMutationResultDto> AddColumnAsync(
        AddColumnRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsSqlServerFamily(request.Connection.DestinationType))
        {
            return new SchemaMutationResultDto(
                false, $"Destination type '{request.Connection.DestinationType}' does not support column creation.");
        }

        string schemaName, tableName, columnName, normalizedDataType;
        int? maxLength;
        string connectionString;
        try
        {
            (schemaName, tableName) = SplitTableName(request.TableName);
            columnName = SqlIdentifier.Validate(request.ColumnName);
            (normalizedDataType, maxLength) = ValidateDataType(request.DataType);
            connectionString = BuildConnectionString(request.Connection);
        }
        catch (Exception exception)
        {
            return new SchemaMutationResultDto(false, exception.Message);
        }

        bool tableCreated;
        try
        {
            await using var connection = await OpenConnectionAsync(
                request.Connection.DestinationType, connectionString, cancellationToken);

            // Adding a column to a resource's default table shouldn't require a separate "create the
            // table first" step — auto-create it (bare Id PK) so "Add column" is self-sufficient, same
            // as "Add table" already is.
            tableCreated = !await TableExistsAsync(connection, schemaName, tableName, cancellationToken);
            if (tableCreated)
            {
                await CreateBareTableAsync(connection, schemaName, tableName, cancellationToken);
            }

            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                ALTER TABLE [{schemaName}].[{tableName}]
                ADD [{columnName}] {normalizedDataType} {(request.IsNullable ? "NULL" : "NOT NULL")};
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            // Column already exists, permission denied, etc. are expected UI outcomes.
            return new SchemaMutationResultDto(false, exception.Message);
        }

        if (tableCreated)
        {
            await RecordSchemaAuditAsync(
                "TableCreated",
                $"Table '{schemaName}.{tableName}' auto-created for a new column.",
                cancellationToken);
        }

        await RecordSchemaAuditAsync(
            "ColumnAdded",
            $"Column '{columnName}' ({normalizedDataType}) added to table '{schemaName}.{tableName}'.",
            cancellationToken);

        var typeFamily = normalizedDataType.Split('(')[0];
        var column = new DestinationColumnSchemaDto(
            columnName, normalizedDataType, MapSqlServerType(typeFamily), request.IsNullable, maxLength);
        return new SchemaMutationResultDto(true, null, column);
    }

    public async Task<SchemaMutationResultDto> CreateTableAsync(
        CreateTableRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsSqlServerFamily(request.Connection.DestinationType))
        {
            return new SchemaMutationResultDto(
                false, $"Destination type '{request.Connection.DestinationType}' does not support table creation.");
        }

        string schemaName, tableName, connectionString;
        try
        {
            (schemaName, tableName) = SplitTableName(request.TableName);
            connectionString = BuildConnectionString(request.Connection);
        }
        catch (Exception exception)
        {
            return new SchemaMutationResultDto(false, exception.Message);
        }

        try
        {
            await using var connection = await OpenConnectionAsync(
                request.Connection.DestinationType, connectionString, cancellationToken);

            if (await TableExistsAsync(connection, schemaName, tableName, cancellationToken))
            {
                return new SchemaMutationResultDto(false, $"Table '{schemaName}.{tableName}' already exists.");
            }

            await CreateBareTableAsync(connection, schemaName, tableName, cancellationToken);
        }
        catch (Exception exception)
        {
            return new SchemaMutationResultDto(false, exception.Message);
        }

        await RecordSchemaAuditAsync(
            "TableCreated",
            $"Table '{schemaName}.{tableName}' created.",
            cancellationToken);

        return new SchemaMutationResultDto(true, null);
    }

    public async Task<SchemaMutationResultDto> DropColumnAsync(
        DropColumnRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsSqlServerFamily(request.Connection.DestinationType))
        {
            return new SchemaMutationResultDto(
                false, $"Destination type '{request.Connection.DestinationType}' does not support dropping columns.");
        }

        string schemaName, tableName, columnName, connectionString;
        try
        {
            (schemaName, tableName) = SplitTableName(request.TableName);
            columnName = SqlIdentifier.Validate(request.ColumnName);
            connectionString = BuildConnectionString(request.Connection);
        }
        catch (Exception exception)
        {
            return new SchemaMutationResultDto(false, exception.Message);
        }

        try
        {
            await using var connection = await OpenConnectionAsync(
                request.Connection.DestinationType, connectionString, cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                ALTER TABLE [{schemaName}].[{tableName}]
                DROP COLUMN [{columnName}];
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            // Table/column missing, column has a constraint/index depending on it, permission denied,
            // etc. are expected UI outcomes.
            return new SchemaMutationResultDto(false, exception.Message);
        }

        await RecordSchemaAuditAsync(
            "ColumnDropped",
            $"Column '{columnName}' permanently dropped from table '{schemaName}.{tableName}'.",
            cancellationToken);

        return new SchemaMutationResultDto(true, null);
    }

    private static async Task<bool> TableExistsAsync(
        DbConnection connection, string schemaName, string tableName, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT OBJECT_ID(N'[{schemaName}].[{tableName}]', N'U');";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null and not DBNull;
    }

    /// <summary>Creates the schema (if missing) and a bare table with just an auto-increment Id primary key.</summary>
    private static async Task CreateBareTableAsync(
        DbConnection connection, string schemaName, string tableName, CancellationToken cancellationToken)
    {
        await using var createSchemaCommand = connection.CreateCommand();
        createSchemaCommand.CommandText = $"""
            IF SCHEMA_ID(N'{schemaName}') IS NULL
            BEGIN
                EXEC(N'CREATE SCHEMA [{schemaName}]')
            END
            """;
        await createSchemaCommand.ExecuteNonQueryAsync(cancellationToken);

        await using var createTableCommand = connection.CreateCommand();
        createTableCommand.CommandText = $"""
            CREATE TABLE [{schemaName}].[{tableName}]
            (
                Id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_{schemaName}_{tableName}_Id PRIMARY KEY
            );
            """;
        await createTableCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task RecordSchemaAuditAsync(string action, string message, CancellationToken cancellationToken)
    {
        var user = _currentUserService.CurrentUser;
        var userId = Guid.TryParse(user.ExternalUserId, out var parsed) ? parsed : (Guid?)null;
        await _userActivityAuditService.RecordAsync(
            new RecordUserActivityRequest(
                UserId: userId,
                UserEmail: user.AuditName,
                Category: UserActivityCategories.Configuration,
                Activity: message,
                Status: UserActivityStatuses.Success,
                IpAddress: user.IpAddress,
                UserAgent: user.UserAgent,
                CorrelationId: user.CorrelationId,
                Module: ModuleDestinationSchema,
                Action: action),
            cancellationToken);
    }

    private static bool IsSqlServerFamily(DestinationType type)
        => type is DestinationType.SqlServer or DestinationType.AzureSql;

    private static (string SchemaName, string TableName) SplitTableName(string tableName)
    {
        var parts = tableName.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts switch
        {
            [var table] => ("dbo", SqlIdentifier.Validate(table)),
            [var schema, var table] => (SqlIdentifier.Validate(schema), SqlIdentifier.Validate(table)),
            _ => throw new InvalidOperationException($"'{tableName}' must be either TableName or SchemaName.TableName.")
        };
    }

    private static (string NormalizedType, int? MaxLength) ValidateDataType(string dataType) =>
        SqlServerDdlTypeValidator.Validate(dataType);

    private async Task<List<DestinationTableSchemaDto>> ReadSchemaAsync(
        DestinationType type,
        string connectionString,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(type, connectionString, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = type is DestinationType.SqlServer or DestinationType.AzureSql
            ? SqlServerColumnsSql
            : InformationSchemaSql;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await ReadColumnsAsync(reader, MapType(type), cancellationToken);
    }

    private static string BuildConnectionString(DestinationConnectionProbeRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.ConnectionString))
        {
            return request.ConnectionString!;
        }

        return request.DestinationType switch
        {
            DestinationType.SqlServer or DestinationType.AzureSql => BuildSqlServerConnectionString(request),
            _ => throw new InvalidOperationException(
                $"Provide a ConnectionString to probe destination type '{request.DestinationType}'."),
        };
    }

    private static string BuildSqlServerConnectionString(DestinationConnectionProbeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Server) || string.IsNullOrWhiteSpace(request.Database))
        {
            throw new InvalidOperationException("Server and Database are required to test a SQL Server connection.");
        }

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = request.Server,
            InitialCatalog = request.Database,
            TrustServerCertificate = request.TrustServerCertificate,
            Encrypt = request.Encrypt,
            ConnectTimeout = 10,
        };

        switch ((request.Authentication ?? "sql-auth").Trim().ToLowerInvariant())
        {
            case "managed-identity":
                builder.Authentication = SqlAuthenticationMethod.ActiveDirectoryManagedIdentity;
                break;
            case "azure-ad":
                builder.Authentication = SqlAuthenticationMethod.ActiveDirectoryDefault;
                break;
            default: // sql-auth
                builder.UserID = request.Username ?? string.Empty;
                builder.Password = request.Password ?? string.Empty;
                break;
        }

        return builder.ConnectionString;
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
