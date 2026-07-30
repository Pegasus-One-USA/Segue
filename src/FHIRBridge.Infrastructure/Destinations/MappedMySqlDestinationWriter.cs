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
}
