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
    // Destination column constraints — never persisted on the mapping profile itself, only filled in at pipeline
    // run time (see ConfiguredPipelineService.MapResourcesAsync) from the destination's live schema, so
    // JsonMappingEngine can reject a value that would overflow the column before it's ever sent to the database.
    int? MaxLength = null,
    int? Precision = null,
    int? Scale = null,
    bool IsEnabled = true);
