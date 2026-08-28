using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Integration.Sql;
using Npgsql;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// One PostgreSQL connection + transaction shared by every DDL action for a single resourceType's import — the
/// PostgreSQL counterpart to <see cref="SqlServerMappingSchemaTransaction"/>/<see cref="MySqlMappingSchemaTransaction"/>.
/// <see cref="CommitAsync"/> must be called explicitly once the caller's own persistence has also succeeded;
/// disposing without committing rolls back everything executed on this transaction.
///
/// Unlike MySQL, PostgreSQL DOES have a real schema layer distinct from the database — a table name here may
/// be schema-qualified ("public.Patient") or bare, defaulting to "public" (PostgreSQL's own real default
/// schema, which always already exists) when unqualified, same convention SqlDestinationSchemaService's own
/// SplitTableName uses for the mapping canvas's ad-hoc connection path.
/// </summary>
public sealed class PostgreSqlMappingSchemaTransaction : IMappingSchemaTransaction
{
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;
    private bool _committed;

    internal PostgreSqlMappingSchemaTransaction(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
    }

    public async Task<bool> TableExistsAsync(string tableName, CancellationToken cancellationToken)
    {
        var (schema, table) = SplitTableName(tableName);

        await using var command = CreateCommand();
        command.CommandText = """
            SELECT COUNT(1) FROM information_schema.tables
            WHERE table_schema = @schema AND table_name = @table;
            """;
        command.Parameters.AddWithValue("@schema", schema);
        command.Parameters.AddWithValue("@table", table);

        var count = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        return count > 0;
    }

    public async Task<bool> ColumnExistsAsync(string tableName, string columnName, CancellationToken cancellationToken)
    {
        var (schema, table) = SplitTableName(tableName);
        var column = SqlIdentifier.Validate(columnName);

        await using var command = CreateCommand();
        command.CommandText = """
            SELECT COUNT(1) FROM information_schema.columns
            WHERE table_schema = @schema AND table_name = @table AND column_name = @column;
            """;
        command.Parameters.AddWithValue("@schema", schema);
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
        var (schema, tableName) = SplitTableName(table.Name);

        var columnDefinitions = new List<string>();
        string? primaryKeyColumn = null;

        foreach (var column in table.Columns)
        {
            var columnName = SqlIdentifier.Validate(column.Name);
            var (normalizedType, _) = PostgreSqlDdlTypeValidator.Validate(column.DataType);

            if (column.IsPrimaryKey)
            {
                primaryKeyColumn = columnName;
                // GENERATED ALWAYS AS IDENTITY is the SQL-standard-compliant, PostgreSQL 10+ auto-increment
                // mechanism — only applied when nothing explicitly maps a value onto this column, same rule
                // MySqlMappingSchemaTransaction/SqlServerMappingSchemaTransaction apply for their own
                // auto-increment/IDENTITY primary keys.
                var identity = IsIntegerFamily(normalizedType) && !explicitlyMappedColumns.Contains(column.Name)
                    ? " GENERATED ALWAYS AS IDENTITY"
                    : string.Empty;
                columnDefinitions.Add($"\"{columnName}\" {normalizedType}{identity} NOT NULL");
            }
            else
            {
                columnDefinitions.Add($"\"{columnName}\" {normalizedType} NULL");
            }
        }

        if (primaryKeyColumn is not null)
        {
            columnDefinitions.Add($"PRIMARY KEY (\"{primaryKeyColumn}\")");
        }

        if (table.Relation is { } relation)
        {
            var childColumn = SqlIdentifier.Validate(relation.ChildColumn);
            var (parentSchema, parentTable) = SplitTableName(relation.ParentTable);
            var parentColumn = SqlIdentifier.Validate(relation.ParentColumn);
            columnDefinitions.Add(
                $"CONSTRAINT \"FK_{schema}_{tableName}_{childColumn}\" FOREIGN KEY (\"{childColumn}\") " +
                $"REFERENCES \"{parentSchema}\".\"{parentTable}\" (\"{parentColumn}\")");
        }

        // ddl-allowed: explicit user action via the mapping-config import wizard's own schema-authoring flow,
        // not automatic writer-side schema mutation — same rationale as SqlServerMappingSchemaTransaction's/
        // MySqlMappingSchemaTransaction's own CREATE TABLE. No CREATE SCHEMA step: "public" (the default
        // when unqualified — see SplitTableName) always already exists; a genuinely non-default schema that
        // doesn't exist just fails this CREATE TABLE with PostgreSQL's own "schema does not exist" error.
        var ddl = $"""
            CREATE TABLE "{schema}"."{tableName}"
            (
                {string.Join(",\n    ", columnDefinitions)}
            );
            """;

        await using var command = CreateCommand();
        command.CommandText = ddl;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AddColumnAsync(string tableName, ColumnToAddDto column, CancellationToken cancellationToken)
    {
        var (schema, table) = SplitTableName(tableName);
        var columnName = SqlIdentifier.Validate(column.Name);
        var (normalizedType, _) = PostgreSqlDdlTypeValidator.Validate(column.DataType);

        await using var command = CreateCommand();
        // ddl-allowed: same rationale as CreateTableAsync above.
        command.CommandText = $"""
            ALTER TABLE "{schema}"."{table}"
            ADD COLUMN "{columnName}" {normalizedType} NULL;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<string?> GetColumnDataTypeAsync(string tableName, string columnName, CancellationToken cancellationToken)
    {
        var (schema, table) = SplitTableName(tableName);
        var column = SqlIdentifier.Validate(columnName);

        await using var command = CreateCommand();
        command.CommandText = """
            SELECT data_type, character_maximum_length, numeric_precision, numeric_scale
            FROM information_schema.columns
            WHERE table_schema = @schema AND table_name = @table AND column_name = @column;
            """;
        command.Parameters.AddWithValue("@schema", schema);
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
            "character varying" or "character" when maxLength is > 0 => $"varchar({maxLength})",
            "numeric" or "decimal" when precision is not null && scale is not null => $"numeric({precision},{scale})",
            _ => dataType,
        };
    }

    public async Task<TableRelationDto?> GetForeignKeyAsync(string tableName, CancellationToken cancellationToken)
    {
        var (schema, table) = SplitTableName(tableName);

        // Unlike MySQL (whose key_column_usage already carries the referenced table/column directly),
        // PostgreSQL's information_schema needs constraint_column_usage joined in to resolve what a foreign
        // key actually references — the standard ANSI pattern for this lookup.
        await using var command = CreateCommand();
        command.CommandText = """
            SELECT kcu.column_name, ccu.table_name, ccu.column_name
            FROM information_schema.table_constraints tc
            JOIN information_schema.key_column_usage kcu
                ON kcu.constraint_name = tc.constraint_name AND kcu.table_schema = tc.table_schema
            JOIN information_schema.constraint_column_usage ccu
                ON ccu.constraint_name = tc.constraint_name AND ccu.table_schema = tc.table_schema
            WHERE tc.constraint_type = 'FOREIGN KEY' AND tc.table_schema = @schema AND tc.table_name = @table;
            """;
        command.Parameters.AddWithValue("@schema", schema);
        command.Parameters.AddWithValue("@table", table);

        var relations = new List<TableRelationDto>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                relations.Add(new TableRelationDto(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        // Only a single, single-column FK is unambiguous — same rule as SqlServerMappingSchemaTransaction/
        // MySqlMappingSchemaTransaction.
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

    private NpgsqlCommand CreateCommand()
    {
        var command = _connection.CreateCommand();
        command.Transaction = _transaction;
        return command;
    }

    private static bool IsIntegerFamily(string normalizedType) =>
        normalizedType is "smallint" or "integer" or "bigint";

    /// <summary>"Patient" -> ("public", "Patient"); "custom.Patient" -> ("custom", "Patient") — PostgreSQL has
    /// a real schema layer (unlike MySQL), so an explicit prefix here IS a genuine schema, not the connected
    /// database's own name — defaults to "public" (PostgreSQL's real default schema) when absent.</summary>
    private static (string Schema, string Table) SplitTableName(string tableName)
    {
        var dot = tableName.LastIndexOf('.');
        return dot >= 0
            ? (SqlIdentifier.Validate(tableName[..dot]), SqlIdentifier.Validate(tableName[(dot + 1)..]))
            : ("public", SqlIdentifier.Validate(tableName));
    }
}
