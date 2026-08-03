namespace FHIRBridge.Application.DTOs;

public sealed record MappedDestinationRecord(
    Guid PipelineRunId,
    string ResourceType,
    string DestinationObject,
    string? SourceResourceId,
    IReadOnlyDictionary<string, object?> Values,
    /// <summary>
    /// The normalized source FHIR resource JSON, carried through so FHIR-native destinations (e.g. a FHIR
    /// repository) can persist a valid resource rather than the flattened tabular <see cref="Values"/>.
    /// Null when the originating flow has no FHIR document (kept optional for backward compatibility).
    /// </summary>
    string? SourceJson = null,
    /// <summary>
    /// Child-table rows (from SeparateDestination-array fields) produced alongside this parent row. Null/empty
    /// when the mapping has no child tables.
    /// </summary>
    IReadOnlyList<MappedChildTableRecord>? ChildTables = null,
    /// <summary>Fields whose value must be resolved against another already-written table before this row is
    /// written (see <see cref="MappedReferenceLookup"/>). Null/empty when the mapping has no reference fields.</summary>
    IReadOnlyList<MappedReferenceLookup>? ReferenceLookups = null);

/// <summary>
/// One field on a <see cref="MappedDestinationRecord"/> whose written value must come from looking up another
/// table rather than being written verbatim — e.g. Observation.PatientId resolved from the id embedded in
/// "$.subject.reference" against Patient.PatientId, to obtain Patient's real (bigint) primary key.
/// </summary>
public sealed record MappedReferenceLookup(
    string TargetField,
    string LookupTable,
    string LookupKeyColumn,
    string? ReferenceId);

/// <summary>
/// One child table's rows for a single parent <see cref="MappedDestinationRecord"/>. The writer links each
/// row back to the parent via <see cref="ForeignKeyColumn"/>, populated with the parent row's own value at
/// <see cref="ParentKeyColumn"/> once the parent write completes.
/// </summary>
public sealed record MappedChildTableRecord(
    string TableName,
    string ForeignKeyColumn,
    string ParentKeyColumn,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows);
