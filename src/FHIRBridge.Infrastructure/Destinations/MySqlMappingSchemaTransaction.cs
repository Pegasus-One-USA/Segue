using System.Text;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Integration.Sql;
using MySqlConnector;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// One MySQL connection + transaction shared by every DDL action for a single resourceType's import — the MySQL
/// counterpart to <see cref="SqlServerMappingSchemaTransaction"/>. <see cref="CommitAsync"/> must be called
/// explicitly once the caller's own persistence has also succeeded; disposing without committing rolls back
/// everything executed on this transaction.
///
/// MySQL has no schema layer distinct from the database (same convention <see cref="MappedMySqlDestinationWriter"/>
/// already uses for the pipeline write path) — every table name here is a bare table name scoped to whichever
/// database the connection string already selects, never a "schema.table" pair. The frontend's own qualify()
/// (field-mapping-summary.model.ts) already only prepends "dbo." for destType 'sql', leaving MySQL table names
/// bare — so no schema-splitting is needed here the way SqlServerMappingSchemaTransaction.SplitTableName does;
/// a defensively-dotted name just has its last segment taken.
/// </summary>
public sealed class MySqlMappingSchemaTransaction : IMappingSchemaTransaction
{
    private readonly MySqlConnection _connection;
    private readonly MySqlTransaction _transaction;
    private bool _committed;

    internal MySqlMappingSchemaTransaction(MySqlConnection connection, MySqlTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
    }

    public async Task<bool> TableExistsAsync(string tableName, CancellationToken cancellationToken)
    {
        var table = BareTableName(tableName);

        await using var command = CreateCommand();
        command.CommandText = """
            SELECT COUNT(1) FROM information_schema.tables
            WHERE table_schema = DATABASE() AND table_name = @table;
            """;
        command.Parameters.AddWithValue("@table", table);

        var count = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        return count > 0;
    }

    public async Task<bool> ColumnExistsAsync(string tableName, string columnName, CancellationToken cancellationToken)
    {
        var table = BareTableName(tableName);
        var column = SqlIdentifier.Validate(columnName);

        await using var command = CreateCommand();
        command.CommandText = """
            SELECT COUNT(1) FROM information_schema.columns
            WHERE table_schema = DATABASE() AND table_name = @table AND column_name = @column;
            """;
        command.Parameters.AddWithValue("@table", table);
        command.Parameters.AddWithValue("@column", column);

        var count = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        return count > 0;
    }

    public async Task CreateTableAsync(
        TableDefinitionDto table,
        IReadOnlySet<string> explicitlyMappedColumns,
        CancellationToken cancellationToken)
    {
        var tableName = BareTableName(table.Name);

        var columnDefinitions = new List<string>();
        string? primaryKeyColumn = null;

        foreach (var column in table.Columns)
        {
            var columnName = SqlIdentifier.Validate(column.Name);
            var (normalizedType, _) = MySqlDdlTypeValidator.Validate(column.DataType);

            if (column.IsPrimaryKey)
            {
                primaryKeyColumn = columnName;
                var autoIncrement = IsIntegerFamily(normalizedType) && !explicitlyMappedColumns.Contains(column.Name)
                    ? " AUTO_INCREMENT"
                    : string.Empty;
                columnDefinitions.Add($"`{columnName}` {normalizedType}{autoIncrement} NOT NULL");
            }
            else
            {
                columnDefinitions.Add($"`{columnName}` {normalizedType} NULL");
            }
        }

        if (primaryKeyColumn is not null)
        {
            columnDefinitions.Add($"PRIMARY KEY (`{primaryKeyColumn}`)");
        }

        if (table.Relation is { } relation)
        {
            var childColumn = SqlIdentifier.Validate(relation.ChildColumn);
            var parentTable = BareTableName(relation.ParentTable);
            var parentColumn = SqlIdentifier.Validate(relation.ParentColumn);
            columnDefinitions.Add(
                $"CONSTRAINT `FK_{tableName}_{childColumn}` FOREIGN KEY (`{childColumn}`) " +
                $"REFERENCES `{parentTable}` (`{parentColumn}`)");
        }

        // ddl-allowed: explicit user action via the mapping-config import wizard's own schema-authoring flow,
        // not automatic writer-side schema mutation — same rationale as SqlServerMappingSchemaTransaction's own
        // CREATE TABLE. No CREATE SCHEMA step here (unlike SQL Server) — MySQL has no schema layer distinct
        // from the database; the connection string's own database is already the target.
        var ddl = new StringBuilder()
            .AppendLine($"CREATE TABLE `{tableName}`")
            .AppendLine("(")
            .AppendLine(string.Join($",{Environment.NewLine}", columnDefinitions))
            .AppendLine(")")
            .ToString();

        await using var command = CreateCommand();
        command.CommandText = ddl;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AddColumnAsync(string tableName, ColumnToAddDto column, CancellationToken cancellationToken)
    {
        var table = BareTableName(tableName);
        var columnName = SqlIdentifier.Validate(column.Name);
        var (normalizedType, _) = MySqlDdlTypeValidator.Validate(column.DataType);

        await using var command = CreateCommand();
        // ddl-allowed: same rationale as CreateTableAsync above.
        command.CommandText = $"""
            ALTER TABLE `{table}`
            ADD `{columnName}` {normalizedType} NULL;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<string?> GetColumnDataTypeAsync(string tableName, string columnName, CancellationToken cancellationToken)
    {
        var table = BareTableName(tableName);
        var column = SqlIdentifier.Validate(columnName);

        await using var command = CreateCommand();
        command.CommandText = """
            SELECT DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, NUMERIC_PRECISION, NUMERIC_SCALE
            FROM information_schema.columns
            WHERE table_schema = DATABASE() AND table_name = @table AND column_name = @column;
            """;
        command.Parameters.AddWithValue("@table", table);
        command.Parameters.AddWithValue("@column", column);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var dataType = reader.GetString(0);
        var maxLength = reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1);
        var precision = reader.IsDBNull(2) ? (int?)null : Convert.ToInt32(reader.GetValue(2));
        var scale = reader.IsDBNull(3) ? (int?)null : Convert.ToInt32(reader.GetValue(3));

        return dataType.ToLowerInvariant() switch
        {
            "varchar" or "char" when maxLength is > 0 => $"{dataType}({maxLength})",
            "decimal" or "numeric" when precision is not null && scale is not null => $"{dataType}({precision},{scale})",
            _ => dataType,
        };
    }

    public async Task<TableRelationDto?> GetForeignKeyAsync(string tableName, CancellationToken cancellationToken)
    {
        var table = BareTableName(tableName);

        // Unlike SQL Server (which needs sys.foreign_keys/sys.foreign_key_columns joined against sys.tables/
        // sys.columns), MySQL's information_schema.key_column_usage already carries the referenced table/column
        // directly on the same row as the constraint's own child column — no join needed.
        await using var command = CreateCommand();
        command.CommandText = """
            SELECT column_name, referenced_table_name, referenced_column_name
            FROM information_schema.key_column_usage
            WHERE table_schema = DATABASE() AND table_name = @table AND referenced_table_name IS NOT NULL;
            """;
        command.Parameters.AddWithValue("@table", table);

        var relations = new List<TableRelationDto>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                relations.Add(new TableRelationDto(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        // Only a single, single-column FK is unambiguous — same rule as SqlServerMappingSchemaTransaction.
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

    private MySqlCommand CreateCommand()
    {
        var command = _connection.CreateCommand();
        command.Transaction = _transaction;
        return command;
    }

    private static bool IsIntegerFamily(string normalizedType) =>
        normalizedType is "tinyint" or "smallint" or "mediumint" or "int" or "integer" or "bigint";

    private static string BareTableName(string tableName)
    {
        var dot = tableName.LastIndexOf('.');
        var bare = dot >= 0 ? tableName[(dot + 1)..] : tableName;
        return SqlIdentifier.Validate(bare);
    }
}
