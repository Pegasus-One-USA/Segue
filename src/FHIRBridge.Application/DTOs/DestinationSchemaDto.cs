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

/// <summary>One user-specified column for <see cref="CreateTableRequest"/> — a name plus a data type string
/// in the same allowed shapes AddColumnRequest.DataType accepts (fixed keyword, sized string, or decimal(p,s)).</summary>
public sealed record TableColumnDefinition(string Name, string DataType);

/// <summary>
/// Creates a new destination table (SQL Server / Azure SQL only). Always gets an auto-increment
/// <c>Id</c> primary key first, followed by any <see cref="Columns"/>. Optionally makes it a child of an
/// already-existing table: supplying <see cref="ParentTable"/> adds a BIGINT <see cref="ForeignKeyColumnName"/>
/// column (defaults to "{ParentTableName}Id") with a FOREIGN KEY REFERENCES constraint against
/// <see cref="ParentTable"/>.<see cref="ParentColumn"/> (defaults to "Id"). Fails if the table already
/// exists, or if a requested parent table doesn't, rather than silently no-op'ing.
/// </summary>
public sealed record CreateTableRequest(
    DestinationConnectionProbeRequest Connection,
    string TableName,
    IReadOnlyList<TableColumnDefinition>? Columns = null,
    string? ParentTable = null,
    string? ParentColumn = null,
    string? ForeignKeyColumnName = null);

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

/// <summary>
/// Changes an already-existing column's data type via a real <c>ALTER TABLE ... ALTER COLUMN</c>, and
/// optionally renames it via <c>sp_rename</c> (only when <see cref="NewColumnName"/> differs from
/// <see cref="ColumnName"/>) — SQL Server / Azure SQL only. The column's existing NULL/NOT NULL
/// constraint is always preserved (the generated ALTER COLUMN statement omits NULL/NOT NULL, which SQL
/// Server keeps unchanged when left unspecified). Fails (does not throw) if the new type is incompatible
/// with existing data, the new name collides with another column, etc.
/// </summary>
public sealed record AlterColumnRequest(
    DestinationConnectionProbeRequest Connection,
    string TableName,
    string ColumnName,
    string NewDataType,
    string? NewColumnName = null);

/// <summary>Result of a schema-mutating action. <see cref="Column"/> is populated by AddColumnAsync (the one
/// column added); <see cref="Table"/> by CreateTableAsync (the full created table, all columns including the
/// FK if any) — so the caller never needs a second round trip to know the table's real shape. AddColumnAsync
/// also populates <see cref="Table"/>, but only when its target table didn't already exist and had to be
/// auto-created (Id + this one column) — the caller has no prior record of that table at all otherwise.</summary>
public sealed record SchemaMutationResultDto(
    bool Success,
    string? Error,
    DestinationColumnSchemaDto? Column = null,
    DestinationTableSchemaDto? Table = null);
