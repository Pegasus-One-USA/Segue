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
    int? MaxLength);

/// <summary>
/// Adds one column to an already-existing destination table via a real ALTER TABLE — SQL Server /
/// Azure SQL only. Same ad-hoc connection model as <see cref="DestinationConnectionProbeRequest"/>;
/// no secret persisted. <see cref="TableName"/> may be schema-qualified ("dbo.Patient") or bare.
/// </summary>
public sealed record AddColumnRequest(
    DestinationConnectionProbeRequest Connection,
    string TableName,
    string ColumnName,
    string DataType,
    bool IsNullable = true);

/// <summary>
/// Creates a new destination table (SQL Server / Azure SQL only) with a single auto-increment
/// <c>Id</c> primary key — additive schema authoring only, no FK/relationship modeling. Fails if the
/// table already exists rather than silently no-op'ing.
/// </summary>
public sealed record CreateTableRequest(
    DestinationConnectionProbeRequest Connection,
    string TableName);

/// <summary>
/// Permanently drops one column from an already-existing destination table via a real
/// <c>ALTER TABLE ... DROP COLUMN</c> — SQL Server / Azure SQL only. Unlike <see cref="AddColumnRequest"/>/
/// <see cref="CreateTableRequest"/>, this is destructive and irreversible: any data in that column is
/// gone. The caller (the mapping canvas) is responsible for confirming this with the user first — this
/// service executes it unconditionally once called.
/// </summary>
public sealed record DropColumnRequest(
    DestinationConnectionProbeRequest Connection,
    string TableName,
    string ColumnName);

/// <summary>Result of a schema-mutating action (add column / create table).</summary>
public sealed record SchemaMutationResultDto(
    bool Success,
    string? Error,
    DestinationColumnSchemaDto? Column = null);
