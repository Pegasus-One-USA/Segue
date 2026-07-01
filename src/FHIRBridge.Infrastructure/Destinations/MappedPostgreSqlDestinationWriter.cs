using System.Data.Common;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.Enums;
using Npgsql;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>Writes mapped records to PostgreSQL (auto-creates schema/table; Insert and Upsert write modes).</summary>
public sealed class MappedPostgreSqlDestinationWriter : RelationalDestinationWriterBase
{
    public MappedPostgreSqlDestinationWriter(ISecretProvider secretProvider) : base(secretProvider)
    {
    }

    protected override DbConnection CreateConnection(string connectionString) => new NpgsqlConnection(connectionString);

    protected override string DefaultSchema => "public";

    protected override string Quote(string identifier) => $"\"{identifier}\"";

    // Stored as text for portability (see base class note).
    protected override string ColumnType(MappingValueType valueType) => "TEXT";

    protected override string? BuildCreateSchemaSql(string schema) => $"CREATE SCHEMA IF NOT EXISTS {Quote(schema)}";

    protected override string BuildCreateTableSql(string schema, string table, IReadOnlyList<string> columnDefinitions)
        => $"CREATE TABLE IF NOT EXISTS {QualifiedName(schema, table)} ({string.Join(", ", columnDefinitions)})";
}
