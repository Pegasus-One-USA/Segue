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
    bool IsUpsertKey = false);
