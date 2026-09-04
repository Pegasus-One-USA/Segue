using System.Data.Common;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Governance;
using MySqlConnector;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Writes mapped records to MySQL (customer-owned schema; Insert and Upsert write modes). MySQL has no schema layer
/// distinct from the database, so the database in the connection string is used and the schema segment is omitted.
/// </summary>
public sealed class MappedMySqlDestinationWriter : RelationalDestinationWriterBase
{
    public MappedMySqlDestinationWriter(ISecretProvider secretProvider, IGlobalExceptionManager? exceptionManager = null)
        : base(secretProvider, exceptionManager)
    {
    }

    protected override DbConnection CreateConnection(string connectionString) => new MySqlConnection(connectionString);

    protected override string DefaultSchema => string.Empty;

    protected override string Quote(string identifier) => $"`{identifier}`";

    // Stored as text for portability (see base class note).
    protected override string ColumnType(MappingValueType valueType) => "LONGTEXT";

    // MySQL has no schema distinct from the database; information_schema.tables scopes by the current database.
    protected override string BuildTableExistsSql(string schema, string table)
        => $"SELECT 1 FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = '{table}'";

    // MySQL's own multi-row upsert syntax — triggers on ANY unique/primary key violation among the inserted rows,
    // not specifically the mapped key column (unlike PostgreSQL's ON CONFLICT, which must name it) — see
    // RelationalDestinationWriterBase.BuildBatchUpsertSql's doc comment for why the caller falls back to the
    // portable per-record path if this throws (e.g. the target table has no unique key at all).
    protected override string BuildBatchUpsertSql(
        string qualifiedTable, IReadOnlyList<string> columns, string keyColumn, IReadOnlyList<string> rowValueClauses)
    {
        var updateColumns = columns
            .Where(column => !string.Equals(column, keyColumn, StringComparison.OrdinalIgnoreCase))
            .Select(column => $"{Quote(column)} = VALUES({Quote(column)})")
            .ToList();
        // Only the key column is mapped — nothing to update on a match, but the clause must still be valid SQL.
        // A harmless self-assignment keeps the statement well-formed without changing any row's data.
        var updateClause = updateColumns.Count > 0
            ? string.Join(", ", updateColumns)
            : $"{Quote(keyColumn)} = {Quote(keyColumn)}";

        return $"INSERT INTO {qualifiedTable} ({string.Join(", ", columns.Select(Quote))}) " +
               $"VALUES {string.Join(", ", rowValueClauses)} " +
               $"ON DUPLICATE KEY UPDATE {updateClause}";
    }
}
