using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.DTOs;

public sealed record MappingFieldDto(
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
    ArrayPolicy ArrayPolicy = ArrayPolicy.Scalar,
    string? Cardinality = null,
    IReadOnlyList<string>? ArrayAncestors = null,
    bool IsUpsertKey = false,
    // Used only when ArrayPolicy is CorrelateByCode: an absolute JsonPath to the code element sharing this field's
    // array ancestor (e.g. "$.component[*].code.coding[*].code" alongside JsonPath
    // "$.component[*].valueQuantity.value"), and the code value that selects which array item's JsonPath value to
    // take (e.g. "8480-6" for BP systolic). Ignored for every other ArrayPolicy.
    string? CorrelationCodeJsonPath = null,
    string? CorrelationCodeValue = null,
    // How CorrelationCodeValue is compared against each array item's sibling code element — "Equals" (the
    // default, including null: every CorrelateByCode field saved before this existed relied on exact match),
    // "Contains" (case-insensitive substring), or "NotEquals". Ignored for every other ArrayPolicy, same as
    // the two members above.
    string? CorrelationCodeOperator = null,
    // Destination column constraints — never persisted on the mapping profile itself, only filled in at pipeline
    // run time (see ConfiguredPipelineService.MapResourcesAsync) from the destination's live schema, so
    // JsonMappingEngine can reject a value that would overflow the column before it's ever sent to the database.
    int? MaxLength = null,
    int? Precision = null,
    int? Scale = null,
    bool IsEnabled = true,
    string? ParentTable = null,
    string? ParentKeyColumn = null,
    string? ForeignKeyColumn = null,
    string? ReferenceLookupTable = null,
    string? ReferenceLookupKeyColumn = null,
    // True when a transformation rule chain — not this raw-copy step — is what produces the destination's real
    // type for this column (see TransformNodeExecutors' rule pre-resolution). JsonMappingEngine then extracts
    // the value WITHOUT coercing it to ValueType and hands it to the rule untouched, because coercion here runs
    // BEFORE the rules do: reading "$.birthDate" as the Integer a DateMathAge rule is going to produce can only
    // fail, and that failure is recorded as a mapping error which ConfiguredPipelineService treats as
    // "skip this whole resource". Never persisted — filled in at run time, by the one execution path that
    // actually applies rules.
    bool DeferTypeToTransform = false);
