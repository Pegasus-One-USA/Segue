using System.Data.Common;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.Enums;
using MySqlConnector;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Writes mapped records to MySQL (auto-creates the table; Insert and Upsert write modes). MySQL has no schema layer
/// distinct from the database, so the database in the connection string is used and the schema segment is omitted.
/// </summary>
public sealed class MappedMySqlDestinationWriter : RelationalDestinationWriterBase
{
    public MappedMySqlDestinationWriter(ISecretProvider secretProvider) : base(secretProvider)
    {
    }

    protected override DbConnection CreateConnection(string connectionString) => new MySqlConnection(connectionString);

    protected override string DefaultSchema => string.Empty;

    protected override string Quote(string identifier) => $"`{identifier}`";

    // Stored as text for portability (see base class note).
    protected override string ColumnType(MappingValueType valueType) => "LONGTEXT";

    protected override string? BuildCreateSchemaSql(string schema) => null;

    protected override string BuildCreateTableSql(string schema, string table, IReadOnlyList<string> columnDefinitions)
        => $"CREATE TABLE IF NOT EXISTS {QualifiedName(schema, table)} ({string.Join(", ", columnDefinitions)})";
}
