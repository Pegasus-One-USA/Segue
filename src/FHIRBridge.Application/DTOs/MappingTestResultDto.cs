namespace FHIRBridge.Application.DTOs;

/// <summary>
/// Result of mapping a single source resource. <see cref="Values"/> is the first parent row
/// (backward-compatible scalar view). <see cref="Rows"/> holds all parent rows produced by
/// RepeatParent fan-out, and <see cref="ChildTables"/> holds SeparateDestination output.
/// </summary>
public sealed record MappingTestResultDto(
    IReadOnlyDictionary<string, object?> Values,
    IReadOnlyList<string> Errors,
    IReadOnlyList<IReadOnlyDictionary<string, object?>>? Rows = null,
    IReadOnlyList<MappingChildTableDto>? ChildTables = null,
    /// <summary>Fields whose value is a FHIR reference (e.g. "Patient/xyz") that must be resolved against
    /// another table's row rather than written verbatim — see <see cref="MappingReferenceLookupDto"/>.</summary>
    IReadOnlyList<MappingReferenceLookupDto>? ReferenceLookups = null,
    /// <summary>The full resolved value list per field, BEFORE the array-policy collapse that produced
    /// <see cref="Values"/> — e.g. every <c>reasonCode[*].text</c> occurrence, not just the one
    /// <see cref="Values"/> kept. Populated for every field regardless of its ArrayPolicy, so a
    /// transform-rule chain that genuinely needs every occurrence (ConcatenationTemplating,
    /// ArrayListOperations) can opt into the real array instead of the already-collapsed scalar — see
    /// MappingNodeExecutor.ApplyTransformRulesAsync / TransformationRuleService.PreviewAsync.</summary>
    IReadOnlyDictionary<string, IReadOnlyList<object?>>? RawArrayValues = null);

public sealed record MappingChildTableDto(
    string Name,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows);

/// <summary>
/// One field on the mapped parent row that needs its written value resolved by looking up another already-
/// written table, rather than trusting whatever <see cref="JsonMappingEngine"/> extracted verbatim (a bare FHIR
/// id string can never satisfy a bigint FK column). <see cref="ReferenceId"/> is the id extracted from the raw
/// reference string (e.g. "Patient/xyz" → "xyz"); null when the source resource had no reference at that path.
/// </summary>
public sealed record MappingReferenceLookupDto(
    string TargetField,
    string LookupTable,
    string LookupKeyColumn,
    string? ReferenceId);
