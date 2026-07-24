using System.Text;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Integration.Sql;
using Microsoft.Data.SqlClient;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// One SQL Server connection + transaction shared by every DDL action for a single resourceType's import.
/// <see cref="CommitAsync"/> must be called explicitly once the caller's own persistence has also succeeded;
/// disposing without committing rolls back everything executed on this transaction.
/// </summary>
public sealed class SqlServerMappingSchemaTransaction : IMappingSchemaTransaction
{
    private readonly SqlConnection _connection;
    private readonly SqlTransaction _transaction;
    private bool _committed;

    internal SqlServerMappingSchemaTransaction(SqlConnection connection, SqlTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
    }

    public async Task<bool> TableExistsAsync(string tableName, CancellationToken cancellationToken)
    {
        var (schemaName, table) = SplitTableName(tableName);
        return await TableExistsAsync(schemaName, table, cancellationToken);
    }

    public async Task<bool> ColumnExistsAsync(string tableName, string columnName, CancellationToken cancellationToken)
    {
        var (schemaName, table) = SplitTableName(tableName);
        var column = SqlIdentifier.Validate(columnName);

        await using var command = CreateCommand();
        command.CommandText = """
            SELECT COUNT(1) FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table AND COLUMN_NAME = @column;
            """;
        command.Parameters.AddWithValue("@schema", schemaName);
        command.Parameters.AddWithValue("@table", table);
        command.Parameters.AddWithValue("@column", column);

        var count = (int)await command.ExecuteScalarAsync(cancellationToken);
        return count > 0;
    }

    public async Task CreateTableAsync(
        TableDefinitionDto table,
        IReadOnlySet<string> explicitlyMappedColumns,
        CancellationToken cancellationToken)
    {
        var (schemaName, tableName) = SplitTableName(table.Name);

        await EnsureSchemaExistsAsync(schemaName, cancellationToken);

        var columnDefinitions = new List<string>();
        string? primaryKeyColumn = null;

        foreach (var column in table.Columns)
        {
            var columnName = SqlIdentifier.Validate(column.Name);
            var (normalizedType, _) = SqlServerDdlTypeValidator.Validate(column.DataType);

            if (column.IsPrimaryKey)
            {
                primaryKeyColumn = columnName;
                var identity = IsIntegerFamily(normalizedType) && !explicitlyMappedColumns.Contains(column.Name)
                    ? " IDENTITY(1,1)"
                    : string.Empty;
                columnDefinitions.Add($"[{columnName}] {normalizedType}{identity} NOT NULL");
            }
            else
            {
                columnDefinitions.Add($"[{columnName}] {normalizedType} NULL");
            }
        }

        if (primaryKeyColumn is not null)
        {
            columnDefinitions.Add(
                $"CONSTRAINT [PK_{schemaName}_{tableName}] PRIMARY KEY ([{primaryKeyColumn}])");
        }

        if (table.Relation is { } relation)
        {
            var childColumn = SqlIdentifier.Validate(relation.ChildColumn);
            var (parentSchema, parentTable) = SplitTableName(relation.ParentTable);
            var parentColumn = SqlIdentifier.Validate(relation.ParentColumn);
            columnDefinitions.Add(
                $"""
                CONSTRAINT [FK_{schemaName}_{tableName}_{childColumn}] FOREIGN KEY ([{childColumn}])
                    REFERENCES [{parentSchema}].[{parentTable}] ([{parentColumn}])
                """);
        }

        var ddl = new StringBuilder()
            .AppendLine($"CREATE TABLE [{schemaName}].[{tableName}]")
            .AppendLine("(")
            .AppendLine(string.Join($",{Environment.NewLine}", columnDefinitions))
            .AppendLine(");")
            .ToString();

        await using var command = CreateCommand();
        command.CommandText = ddl;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AddColumnAsync(string tableName, ColumnToAddDto column, CancellationToken cancellationToken)
    {
        var (schemaName, table) = SplitTableName(tableName);
        var columnName = SqlIdentifier.Validate(column.Name);
        var (normalizedType, _) = SqlServerDdlTypeValidator.Validate(column.DataType);

        await using var command = CreateCommand();
        command.CommandText = $"""
            ALTER TABLE [{schemaName}].[{table}]
            ADD [{columnName}] {normalizedType} NULL;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<string?> GetColumnDataTypeAsync(string tableName, string columnName, CancellationToken cancellationToken)
    {
        var (schemaName, table) = SplitTableName(tableName);
        var column = SqlIdentifier.Validate(columnName);

        await using var command = CreateCommand();
        command.CommandText = """
            SELECT DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, NUMERIC_PRECISION, NUMERIC_SCALE
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table AND COLUMN_NAME = @column;
            """;
        command.Parameters.AddWithValue("@schema", schemaName);
        command.Parameters.AddWithValue("@table", table);
        command.Parameters.AddWithValue("@column", column);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var dataType = reader.GetString(0);
        var maxLength = reader.IsDBNull(1) ? (int?)null : reader.GetInt32(1);
        var precision = reader.IsDBNull(2) ? (int?)null : Convert.ToInt32(reader.GetValue(2));
        var scale = reader.IsDBNull(3) ? (int?)null : Convert.ToInt32(reader.GetValue(3));

        return dataType.ToLowerInvariant() switch
        {
            "nvarchar" or "varchar" or "char" or "nchar" when maxLength is -1 => $"{dataType}(max)",
            "nvarchar" or "varchar" or "char" or "nchar" when maxLength is > 0 => $"{dataType}({maxLength})",
            "decimal" or "numeric" when precision is not null && scale is not null => $"{dataType}({precision},{scale})",
            _ => dataType
        };
    }

    public async Task<TableRelationDto?> GetForeignKeyAsync(string tableName, CancellationToken cancellationToken)
    {
        var (schemaName, table) = SplitTableName(tableName);

        await using var command = CreateCommand();
        command.CommandText = """
            SELECT
                cp.name AS ChildColumn,
                SCHEMA_NAME(tp.schema_id) AS ParentSchema,
                tp.name AS ParentTable,
                cr.name AS ParentColumn
            FROM sys.foreign_keys fk
            JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
            JOIN sys.tables tp ON tp.object_id = fk.referenced_object_id
            JOIN sys.columns cp ON cp.object_id = fkc.parent_object_id AND cp.column_id = fkc.parent_column_id
            JOIN sys.columns cr ON cr.object_id = fkc.referenced_object_id AND cr.column_id = fkc.referenced_column_id
            WHERE fk.parent_object_id = OBJECT_ID(@fullTableName, N'U');
            """;
        command.Parameters.AddWithValue("@fullTableName", $"[{schemaName}].[{table}]");

        var relations = new List<TableRelationDto>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                relations.Add(new TableRelationDto(
                    reader.GetString(0), $"{reader.GetString(1)}.{reader.GetString(2)}", reader.GetString(3)));
            }
        }

        // Only a single, single-column FK is unambiguous — a composite key or multiple FKs on this table
        // can't be resolved to "the" parent relation without more information, so leave it null rather than guess.
        return relations.Count == 1 ? relations[0] : null;
    }

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        await _transaction.CommitAsync(cancellationToken);
        _committed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_committed)
        {
            await _transaction.RollbackAsync();
        }

        await _transaction.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private SqlCommand CreateCommand()
    {
        var command = _connection.CreateCommand();
        command.Transaction = _transaction;
        return command;
    }

    private async Task EnsureSchemaExistsAsync(string schemaName, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand();
        command.CommandText = $"""
            IF SCHEMA_ID(N'{schemaName}') IS NULL
            BEGIN
                EXEC(N'CREATE SCHEMA [{schemaName}]')
            END
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<bool> TableExistsAsync(string schemaName, string tableName, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand();
        command.CommandText = "SELECT OBJECT_ID(@fullName, N'U');";
        command.Parameters.AddWithValue("@fullName", $"[{schemaName}].[{tableName}]");
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null and not DBNull;
    }

    private static bool IsIntegerFamily(string normalizedType) =>
        normalizedType is "int" or "bigint" or "smallint" or "tinyint";

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
}
