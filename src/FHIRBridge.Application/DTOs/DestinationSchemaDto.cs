using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.DTOs;

public sealed record DestinationSchemaDto(
    Guid DestinationId,
    IReadOnlyList<DestinationTableSchemaDto> Tables);

/// <summary>
/// Ad-hoc connection details for a not-yet-saved destination, used by the builder to test the connection
/// and load real tables/columns. Either supply <see cref="ConnectionString"/> directly, or the discrete
/// server/database/auth fields (SQL Server / Azure SQL). No secret is persisted.
/// </summary>
public sealed record DestinationConnectionProbeRequest(
    DestinationType DestinationType,
    string? Server = null,
    string? Database = null,
    string? Authentication = null,
    string? Username = null,
    string? Password = null,
    bool TrustServerCertificate = true,
    bool Encrypt = true,
    string? ConnectionString = null);

/// <summary>Result of a connection probe: whether it connected, any error, and the introspected tables.</summary>
public sealed record DestinationSchemaProbeDto(
    bool Connected,
    string? Error,
    IReadOnlyList<DestinationTableSchemaDto> Tables);

public sealed record DestinationTableSchemaDto(
    string SchemaName,
    string TableName,
    string FullName,
    IReadOnlyList<DestinationColumnSchemaDto> Columns);

public sealed record DestinationColumnSchemaDto(
    string Name,
    string DataType,
    string MappingValueType,
    bool IsNullable,
    int? MaxLength,
    bool IsPrimaryKey = false,
    bool IsUnique = false);
