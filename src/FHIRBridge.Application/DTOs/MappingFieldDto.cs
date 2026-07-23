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
    string? ParentTable = null,
    string? ParentKeyColumn = null,
    string? ForeignKeyColumn = null);
