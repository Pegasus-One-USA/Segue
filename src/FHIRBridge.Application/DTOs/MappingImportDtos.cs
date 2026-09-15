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
    IReadOnlyList<TargetTableDto> Tables,
    /// <summary>The MappingProfile this resource's own node last saved, when known — round-tripped by the
    /// caller from whatever it was handed back on a prior import (see MappingProfileImportResourceResult).
    /// When present, the import always updates THIS exact profile rather than searching for "the" profile
    /// matching (ResourceType, SourceConnectionId, DestinationId): that triple is not a safe identity — two
    /// unrelated workflows sharing the same source connection, destination, and resource type would
    /// otherwise resolve to and silently overwrite the SAME profile row (see the "Invalid column name"
    /// incident this replaces). Null only for a genuinely first-ever save, which always creates a new
    /// profile rather than adopting one that happens to match the triple.</summary>
    Guid? ExistingMappingProfileId = null);

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

/// <summary><see cref="Mode"/> is one of "directField" | "joinedFields" | "wholeNodeAsJson" | "default".</summary>
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
    ReferenceLookupDto? ReferenceLookup = null,
    /// <summary>Mode "default" only — which @token this column always resolves to (e.g. "@default",
    /// "@now") — see JsonMappingEngine.IsSystemToken / field-mapping-model.ts's DefaultValueToken.</summary>
    string? DefaultToken = null,
    /// <summary>Mode "default" only, and only meaningful when DefaultToken is "@default" — the literal
    /// text written for every record.</summary>
    string? DefaultValue = null,
    /// <summary>Mode "default" only — the MappingValueType this column writes, since there's no source
    /// field to derive one from.</summary>
    string? DefaultValueType = null);

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
