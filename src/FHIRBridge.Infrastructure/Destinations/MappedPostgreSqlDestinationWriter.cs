using System.Data.Common;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Governance;
using Npgsql;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>Writes mapped records to PostgreSQL (customer-owned schema; Insert and Upsert write modes).</summary>
public sealed class MappedPostgreSqlDestinationWriter : RelationalDestinationWriterBase
{
    public MappedPostgreSqlDestinationWriter(ISecretProvider secretProvider, IGlobalExceptionManager? exceptionManager = null)
        : base(secretProvider, exceptionManager)
    {
    }

    protected override DbConnection CreateConnection(string connectionString) => new NpgsqlConnection(connectionString);

    protected override string DefaultSchema => "public";

    protected override string Quote(string identifier) => $"\"{identifier}\"";

    // Stored as text for portability (see base class note).
    protected override string ColumnType(MappingValueType valueType) => "TEXT";

    protected override string BuildTableExistsSql(string schema, string table)
        => $"SELECT 1 FROM information_schema.tables WHERE table_schema = '{schema}' AND table_name = '{table}'";

    // PostgreSQL's own multi-row upsert syntax — unlike MySQL's ON DUPLICATE KEY UPDATE, ON CONFLICT must name the
    // conflict target column, and PostgreSQL requires a unique/primary key constraint on it to even parse this
    // (raises 42P10 otherwise). See RelationalDestinationWriterBase.BuildBatchUpsertSql's doc comment for why the
    // caller falls back to the portable per-record path when that happens.
    protected override string BuildBatchUpsertSql(
        string qualifiedTable, IReadOnlyList<string> columns, string keyColumn, IReadOnlyList<string> rowValueClauses)
    {
        var updateColumns = columns
            .Where(column => !string.Equals(column, keyColumn, StringComparison.OrdinalIgnoreCase))
            .Select(column => $"{Quote(column)} = EXCLUDED.{Quote(column)}")
            .ToList();
        // Only the key column is mapped — nothing to update on a match, but the clause must still be valid SQL.
        // DO NOTHING keeps the statement well-formed without changing any row's data.
        if (updateColumns.Count == 0)
        {
            return $"INSERT INTO {qualifiedTable} ({string.Join(", ", columns.Select(Quote))}) " +
                   $"VALUES {string.Join(", ", rowValueClauses)} " +
                   $"ON CONFLICT ({Quote(keyColumn)}) DO NOTHING";
        }

        return $"INSERT INTO {qualifiedTable} ({string.Join(", ", columns.Select(Quote))}) " +
               $"VALUES {string.Join(", ", rowValueClauses)} " +
               $"ON CONFLICT ({Quote(keyColumn)}) DO UPDATE SET {string.Join(", ", updateColumns)}";
    }
}
