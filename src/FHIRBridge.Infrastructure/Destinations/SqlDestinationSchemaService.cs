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

        try
        {
            await using var connection = await OpenConnectionAsync(
                request.Connection.DestinationType, connectionString, cancellationToken);

            // Never auto-create the table here — a column can only be added to a table the user
            // explicitly created (via "Create a new table…") or that already exists for real. Silently
            // creating a bare table just because its name was guessed and doesn't exist yet surprises the
            // user with schema changes they never asked for.
            if (!await TableExistsAsync(connection, schemaName, tableName, cancellationToken))
            {
                return new SchemaMutationResultDto(
                    false, $"Table '{schemaName}.{tableName}' does not exist. Create it first via \"Create a new table…\".");
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
        var columns = new List<(string Name, string NormalizedType, int? MaxLength)>();
        (string SchemaName, string TableName, string ColumnName)? parent = null;
        string? fkColumnName = null;
        try
        {
            (schemaName, tableName) = SplitTableName(request.TableName);
            connectionString = BuildConnectionString(request.Connection);

            foreach (var column in request.Columns ?? [])
            {
                var name = SqlIdentifier.Validate(column.Name);
                var (normalizedType, maxLength) = ValidateDataType(column.DataType);
                columns.Add((name, normalizedType, maxLength));
            }

            if (!string.IsNullOrWhiteSpace(request.ParentTable))
            {
                var (parentSchema, parentTableName) = SplitTableName(request.ParentTable);
                var parentColumn = SqlIdentifier.Validate(
                    string.IsNullOrWhiteSpace(request.ParentColumn) ? "Id" : request.ParentColumn);
                parent = (parentSchema, parentTableName, parentColumn);
                fkColumnName = SqlIdentifier.Validate(
                    string.IsNullOrWhiteSpace(request.ForeignKeyColumnName)
                        ? $"{parentTableName}Id"
                        : request.ForeignKeyColumnName);
            }
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

            if (parent is { } p && !await TableExistsAsync(connection, p.SchemaName, p.TableName, cancellationToken))
            {
                return new SchemaMutationResultDto(false, $"Parent table '{p.SchemaName}.{p.TableName}' was not found.");
            }

            await using var createSchemaCommand = connection.CreateCommand();
            createSchemaCommand.CommandText = $"""
                IF SCHEMA_ID(N'{schemaName}') IS NULL
                BEGIN
                    EXEC(N'CREATE SCHEMA [{schemaName}]')
                END
                """;
            await createSchemaCommand.ExecuteNonQueryAsync(cancellationToken);

            var columnDefinitions = new List<string>
            {
                $"Id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_{schemaName}_{tableName}_Id PRIMARY KEY",
            };
            columnDefinitions.AddRange(columns.Select(c => $"[{c.Name}] {c.NormalizedType} NULL"));
            if (parent is { } fk)
            {
                columnDefinitions.Add(
                    $"[{fkColumnName}] BIGINT NOT NULL " +
                    $"CONSTRAINT FK_{schemaName}_{tableName}_{fkColumnName} " +
                    $"REFERENCES [{fk.SchemaName}].[{fk.TableName}]([{fk.ColumnName}])");
            }

            await using var createTableCommand = connection.CreateCommand();
            createTableCommand.CommandText = $"""
                CREATE TABLE [{schemaName}].[{tableName}]
                (
                    {string.Join(",\n    ", columnDefinitions)}
                );
                """;
            await createTableCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            return new SchemaMutationResultDto(false, exception.Message);
        }

        await RecordSchemaAuditAsync(
            "TableCreated",
            $"Table '{schemaName}.{tableName}' created"
                + (parent is { } auditParent ? $" as a child of '{auditParent.SchemaName}.{auditParent.TableName}'." : "."),
            cancellationToken);

        var resultColumns = new List<DestinationColumnSchemaDto>
        {
            new("Id", "bigint", "Integer", false, null, IsPrimaryKey: true),
        };
        resultColumns.AddRange(columns.Select(c =>
            new DestinationColumnSchemaDto(c.Name, c.NormalizedType, MapSqlServerType(c.NormalizedType.Split('(')[0]), true, c.MaxLength)));
        if (parent is { } fkParent)
        {
            resultColumns.Add(new DestinationColumnSchemaDto(
                fkColumnName!, "bigint", "Integer", false, null,
                IsForeignKey: true, References: $"{fkParent.SchemaName}.{fkParent.TableName}.{fkParent.ColumnName}"));
        }

        var table = new DestinationTableSchemaDto(schemaName, tableName, $"{schemaName}.{tableName}", resultColumns);
        return new SchemaMutationResultDto(true, null, Table: table);
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

    public async Task<SchemaMutationResultDto> AlterColumnAsync(
        AlterColumnRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsSqlServerFamily(request.Connection.DestinationType))
        {
            return new SchemaMutationResultDto(
                false, $"Destination type '{request.Connection.DestinationType}' does not support altering columns.");
        }

        string schemaName, tableName, columnName, normalizedDataType, connectionString;
        int? maxLength;
        string? newColumnName = null;
        try
        {
            (schemaName, tableName) = SplitTableName(request.TableName);
            columnName = SqlIdentifier.Validate(request.ColumnName);
            (normalizedDataType, maxLength) = ValidateDataType(request.NewDataType);
            if (!string.IsNullOrWhiteSpace(request.NewColumnName))
            {
                newColumnName = SqlIdentifier.Validate(request.NewColumnName);
            }
            connectionString = BuildConnectionString(request.Connection);
        }
        catch (Exception exception)
        {
            return new SchemaMutationResultDto(false, exception.Message);
        }

        var finalColumnName = columnName;
        bool isNullable;
        bool isPrimaryKey;
        string? references;
        try
        {
            await using var connection = await OpenConnectionAsync(
                request.Connection.DestinationType, connectionString, cancellationToken);

            await using var alterCommand = connection.CreateCommand();
            alterCommand.CommandText = $"""
                ALTER TABLE [{schemaName}].[{tableName}]
                ALTER COLUMN [{columnName}] {normalizedDataType};
                """;
            await alterCommand.ExecuteNonQueryAsync(cancellationToken);

            if (newColumnName is not null
                && !string.Equals(newColumnName, columnName, StringComparison.OrdinalIgnoreCase))
            {
                await using var renameCommand = connection.CreateCommand();
                renameCommand.CommandText = "EXEC sp_rename @objname, @newname, N'COLUMN';";
                AddParameter(renameCommand, "@objname", $"{schemaName}.{tableName}.{columnName}");
                AddParameter(renameCommand, "@newname", newColumnName);
                await renameCommand.ExecuteNonQueryAsync(cancellationToken);
                finalColumnName = newColumnName;
            }

            isNullable = await GetColumnNullableAsync(connection, schemaName, tableName, finalColumnName, cancellationToken);
            (isPrimaryKey, references) = await GetColumnKeyInfoAsync(connection, schemaName, tableName, finalColumnName, cancellationToken);
        }
        catch (Exception exception)
        {
            // Incompatible type conversion, new name collides with an existing column, table/column
            // missing, permission denied, etc. are expected UI outcomes.
            return new SchemaMutationResultDto(false, exception.Message);
        }

        await RecordSchemaAuditAsync(
            "ColumnAltered",
            finalColumnName == columnName
                ? $"Column '{columnName}' on table '{schemaName}.{tableName}' changed to {normalizedDataType}."
                : $"Column '{columnName}' on table '{schemaName}.{tableName}' renamed to '{finalColumnName}' and changed to {normalizedDataType}.",
            cancellationToken);

        var typeFamily = normalizedDataType.Split('(')[0];
        var column = new DestinationColumnSchemaDto(
            finalColumnName, normalizedDataType, MapSqlServerType(typeFamily), isNullable, maxLength,
            isPrimaryKey, references is not null, references);
        return new SchemaMutationResultDto(true, null, column);
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    /// <summary>Definitive post-ALTER nullability, read back from the catalog rather than assumed — the
    /// ALTER COLUMN statement above deliberately omits NULL/NOT NULL to preserve whatever it already
    /// was, so this is the only way to know what that ended up being.</summary>
    private static async Task<bool> GetColumnNullableAsync(
        DbConnection connection, string schemaName, string tableName, string columnName, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT IS_NULLABLE FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table AND COLUMN_NAME = @column;
            """;
        AddParameter(command, "@schema", schemaName);
        AddParameter(command, "@table", tableName);
        AddParameter(command, "@column", columnName);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is string s && string.Equals(s, "YES", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> TableExistsAsync(
        DbConnection connection, string schemaName, string tableName, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT OBJECT_ID(N'[{schemaName}].[{tableName}]', N'U');";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null and not DBNull;
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
        var isSqlServerFamily = type is DestinationType.SqlServer or DestinationType.AzureSql;

        // PK/FK constraints are only readable from SQL Server's own sys.* catalog views — PostgreSQL/MySQL
        // columns keep IsPrimaryKey=false/References=null (see DestinationColumnSchemaDto's own doc comment).
        var keyMetadata = isSqlServerFamily
            ? await ReadSqlServerKeyMetadataAsync(connection, cancellationToken)
            : new Dictionary<string, (bool IsPrimaryKey, string? References)>();

        await using var command = connection.CreateCommand();
        command.CommandText = isSqlServerFamily ? SqlServerColumnsSql : InformationSchemaSql;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await ReadColumnsAsync(reader, MapType(type), keyMetadata, cancellationToken);
    }

    /// <summary>
    /// One row per column that is a primary key and/or a foreign key, across every table in the database —
    /// read once per probe/schema-load and merged into ReadColumnsAsync's per-column output by "schema.table.column"
    /// key, rather than guessing PK/FK from a column's name (e.g. "Id", "{Table}Id"). Composite keys spanning
    /// several columns still report each participating column's own IsPrimaryKey/References independently.
    /// </summary>
    private static async Task<Dictionary<string, (bool IsPrimaryKey, string? References)>> ReadSqlServerKeyMetadataAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        var metadata = new Dictionary<string, (bool IsPrimaryKey, string? References)>(StringComparer.OrdinalIgnoreCase);

        await using var command = connection.CreateCommand();
        command.CommandText = SqlServerBulkKeyMetadataSql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var key = $"{reader.GetString(0)}.{reader.GetString(1)}.{reader.GetString(2)}";
            var isPrimaryKey = reader.GetInt32(3) == 1;
            string? references = reader.IsDBNull(4)
                ? null
                : $"{reader.GetString(4)}.{reader.GetString(5)}.{reader.GetString(6)}";

            metadata[key] = metadata.TryGetValue(key, out var existing)
                ? (existing.IsPrimaryKey || isPrimaryKey, existing.References ?? references)
                : (isPrimaryKey, references);
        }

        return metadata;
    }

    /// <summary>Same PK/FK lookup as <see cref="ReadSqlServerKeyMetadataAsync"/>, scoped to one already-known
    /// column — used right after AlterColumnAsync's rename/retype, where re-running the bulk, whole-database
    /// query would be wasteful for a single column whose key status could only be reported stale otherwise.</summary>
    private static async Task<(bool IsPrimaryKey, string? References)> GetColumnKeyInfoAsync(
        DbConnection connection,
        string schemaName,
        string tableName,
        string columnName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = SqlServerSingleColumnKeyMetadataSql;
        AddParameter(command, "@schema", schemaName);
        AddParameter(command, "@table", tableName);
        AddParameter(command, "@column", columnName);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return (false, null);
        }

        var isPrimaryKey = reader.GetInt32(0) == 1;
        string? references = reader.IsDBNull(1) ? null : $"{reader.GetString(1)}.{reader.GetString(2)}.{reader.GetString(3)}";
        return (isPrimaryKey, references);
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

    // Only columns that ARE a primary key and/or a foreign key are returned — every other column is
    // implicitly neither, so the result set stays small regardless of database size. ReferencedSchema/
    // ReferencedTable/ReferencedColumn are NULL for a plain (non-FK) primary key column.
    private const string SqlServerBulkKeyMetadataSql = """
        SELECT s.name, t.name, c.name,
               CASE WHEN pk.column_id IS NOT NULL THEN 1 ELSE 0 END,
               rs.name, rt.name, rc.name
        FROM sys.columns c
        JOIN sys.tables t ON t.object_id = c.object_id
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        LEFT JOIN (
            SELECT ic.object_id, ic.column_id
            FROM sys.index_columns ic
            JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
            WHERE i.is_primary_key = 1
        ) pk ON pk.object_id = c.object_id AND pk.column_id = c.column_id
        LEFT JOIN sys.foreign_key_columns fkc ON fkc.parent_object_id = c.object_id AND fkc.parent_column_id = c.column_id
        LEFT JOIN sys.tables rt ON rt.object_id = fkc.referenced_object_id
        LEFT JOIN sys.schemas rs ON rs.schema_id = rt.schema_id
        LEFT JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
        WHERE s.name NOT IN ('sys', 'INFORMATION_SCHEMA') AND (pk.column_id IS NOT NULL OR fkc.parent_column_id IS NOT NULL);
        """;

    // Same PK/FK shape as SqlServerBulkKeyMetadataSql, scoped to one column via @schema/@table/@column.
    private const string SqlServerSingleColumnKeyMetadataSql = """
        SELECT CASE WHEN pk.column_id IS NOT NULL THEN 1 ELSE 0 END,
               rs.name, rt.name, rc.name
        FROM sys.columns c
        JOIN sys.tables t ON t.object_id = c.object_id AND t.name = @table
        JOIN sys.schemas s ON s.schema_id = t.schema_id AND s.name = @schema
        LEFT JOIN (
            SELECT ic.object_id, ic.column_id
            FROM sys.index_columns ic
            JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
            WHERE i.is_primary_key = 1
        ) pk ON pk.object_id = c.object_id AND pk.column_id = c.column_id
        LEFT JOIN sys.foreign_key_columns fkc ON fkc.parent_object_id = c.object_id AND fkc.parent_column_id = c.column_id
        LEFT JOIN sys.tables rt ON rt.object_id = fkc.referenced_object_id
        LEFT JOIN sys.schemas rs ON rs.schema_id = rt.schema_id
        LEFT JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
        WHERE c.name = @column;
        """;

    private static async Task<List<DestinationTableSchemaDto>> ReadColumnsAsync(
        DbDataReader reader,
        Func<string, string> mapType,
        IReadOnlyDictionary<string, (bool IsPrimaryKey, string? References)> keyMetadata,
        CancellationToken cancellationToken)
    {
        var tables = new Dictionary<string, List<DestinationColumnSchemaDto>>(StringComparer.OrdinalIgnoreCase);
        var tableNames = new Dictionary<string, (string SchemaName, string TableName)>(StringComparer.OrdinalIgnoreCase);

        while (await reader.ReadAsync(cancellationToken))
        {
            var schemaName = reader.GetString(0);
            var tableName = reader.GetString(1);
            var columnName = reader.GetString(2);
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

            var (isPrimaryKey, references) = keyMetadata.TryGetValue($"{fullName}.{columnName}", out var key)
                ? key
                : (false, null);

            columns.Add(new DestinationColumnSchemaDto(
                columnName,
                dataType,
                mapType(dataType),
                string.Equals(reader.GetString(4), "YES", StringComparison.OrdinalIgnoreCase),
                maxLength,
                isPrimaryKey,
                references is not null,
                references));
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
