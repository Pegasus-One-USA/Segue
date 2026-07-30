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
}
