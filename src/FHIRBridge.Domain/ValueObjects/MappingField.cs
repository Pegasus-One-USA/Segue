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
    bool IsUpsertKey = false,
    // Used only when ArrayPolicy is CorrelateByCode: an absolute JsonPath to the code element sharing this field's
    // array ancestor (e.g. "$.component[*].code.coding[*].code" alongside JsonPath
    // "$.component[*].valueQuantity.value"), and the code value that selects which array item's JsonPath value to
    // take (e.g. "8480-6" for BP systolic). Ignored for every other ArrayPolicy.
    string? CorrelationCodeJsonPath = null,
    string? CorrelationCodeValue = null,
    // How CorrelationCodeValue is compared against each array item's sibling code element — "Equals" (the
    // default, including null: every CorrelateByCode field saved before this existed relied on exact
    // match), "Contains" (case-insensitive substring), or "NotEquals". Mirrors MappingFieldDto's own member
    // of the same name exactly (see JsonMappingEngine.MatchesCorrelationOperator); ignored for every other
    // ArrayPolicy, same as the two members above.
    string? CorrelationCodeOperator = null,
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
