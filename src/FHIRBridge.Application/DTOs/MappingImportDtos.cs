using System.Text.Json;

namespace FHIRBridge.Application.DTOs;

/// <summary>
/// Payload posted by the Workflow Builder's "Update" button — one entry per FHIR resourceType, each
/// describing the destination schema changes it needs plus its source-JSON-path → destination-column
/// field mappings. See <c>MappingImport-API-Spec.md</c> for the full contract. Deserialized with
/// permissive options (unknown properties ignored) since the contract is expected to evolve.
/// </summary>
public sealed record MappingImportRequestDto(
    string Source,
    string Destination,
    Guid SourceConnectionId,
    Guid DestinationId,
    IReadOnlyList<ResourceMappingDto> Mappings);

/// <summary>
/// <see cref="Destination"/> is carried through but never read by the import service (the root-level
/// <see cref="MappingImportRequestDto.DestinationId"/> is authoritative) — kept as a permissive
/// <see cref="JsonElement"/> rather than <c>string</c> since real UI payloads send an object here
/// (<c>{"type":"mssql","label":"MSSQL Server"}</c>), not the plain string the spec doc's example showed.
/// </summary>
public sealed record ResourceMappingDto(
    string ResourceType,
    int Rank,
    DateTime GeneratedAt,
    SchemaChangesDto SchemaChanges,
    IReadOnlyList<ProcessingOrderStepDto> ProcessingOrder,
    JsonElement? Destination,
    IReadOnlyList<TargetTableDto> Tables);

public sealed record SchemaChangesDto(
    IReadOnlyList<TableDefinitionDto> TablesToCreate,
    IReadOnlyList<ColumnToAddDto> ColumnsToAdd,
    string? Summary);

public sealed record TableDefinitionDto(
    string Name,
    TableRelationDto? Relation,
    IReadOnlyList<ColumnDefinitionDto> Columns);

public sealed record TableRelationDto(string ChildColumn, string ParentTable, string ParentColumn);

public sealed record ColumnDefinitionDto(
    string Name,
    string DataType,
    bool IsPrimaryKey,
    bool IsForeignKey,
    string? References);

public sealed record ColumnToAddDto(string Table, string Name, string DataType);

public sealed record ProcessingOrderStepDto(int Step, string Table, int Level, string? DependsOn, string? Note);

public sealed record TargetTableDto(
    string Name,
    bool IsNew,
    TableRelationDto? Relation,
    IReadOnlyList<ColumnMappingDto> Columns);

/// <summary><see cref="Mode"/> is one of "directField" | "joinedFields" | "wholeNodeAsJson".</summary>
public sealed record ColumnMappingDto(
    string Column,
    string Mode,
    IReadOnlyList<string>? Sources,
    string? SourceNode,
    string? Delimiter,
    InstanceSelectorDto? Instance,
    /// <summary>Non-null when this column is a FHIR reference (e.g. "$.subject.reference") that must be
    /// resolved against another table's row at write time rather than written verbatim — see
    /// <see cref="Domain.ValueObjects.MappingField.ReferenceLookupTable"/>.</summary>
    ReferenceLookupDto? ReferenceLookup = null);

/// <summary>Where to resolve a reference column's extracted id against: <see cref="Table"/> is the other
/// mapped resource's own destination table, <see cref="KeyColumn"/> is the column there holding that
/// resource's own FHIR id (its own "$.id"-mapped field).</summary>
public sealed record ReferenceLookupDto(string Table, string KeyColumn);

/// <summary><see cref="Type"/> is one of "all" | "first" | "nth" | "criteria".</summary>
public sealed record InstanceSelectorDto(
    string ArrayContext,
    string Type,
    int? N,
    string? Field,
    string? Op,
    string? Value,
    string? Aggregate);

// ── Result ──────────────────────────────────────────────────────────────────

public sealed record MappingImportResultDto(IReadOnlyList<ResourceImportResultDto> Profiles);

public sealed record ResourceImportResultDto(
    string ResourceType,
    Guid MappingProfileId,
    IReadOnlyList<string> TablesCreated,
    IReadOnlyList<string> TablesSkippedAlreadyExisted,
    IReadOnlyList<ColumnAddedDto> ColumnsAdded,
    IReadOnlyList<ColumnAddedDto> ColumnsSkippedAlreadyExisted,
    int FieldsInserted,
    IReadOnlyList<string> Warnings);

public sealed record ColumnAddedDto(string Table, string Column);
