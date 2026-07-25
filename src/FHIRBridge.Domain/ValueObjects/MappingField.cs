using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Domain.ValueObjects;

public sealed record MappingField(
    string TargetField,
    string JsonPath,
    MappingValueType ValueType,
    bool IsRequired,
    string? DefaultValue,
    string? Format,
    string? ResourceType = null,
    string? DestinationObject = null,
    string? NormalizationType = null,
    string? TerminologySystemJsonPath = null,
    string? TerminologyCodeJsonPath = null,
    bool IsEnabled = true,
    ArrayPolicy ArrayPolicy = ArrayPolicy.Scalar,
    string? Cardinality = null,
    string? ArrayAncestors = null,
    string? ParentTable = null,
    string? ParentKeyColumn = null,
    string? ForeignKeyColumn = null,
    /// <summary>Non-null marks this field as a FHIR reference (e.g. "$.subject.reference" = "Patient/xyz") that
    /// must be resolved against another already-written table rather than written verbatim — the name of that
    /// table, e.g. "Patient".</summary>
    string? ReferenceLookupTable = null,
    /// <summary>The column in <see cref="ReferenceLookupTable"/> holding the referenced resource's own FHIR id
    /// (e.g. "PatientId") — matched against the id extracted from the reference string to find that row's real
    /// primary key, which becomes this field's actual written value.</summary>
    string? ReferenceLookupKeyColumn = null);
