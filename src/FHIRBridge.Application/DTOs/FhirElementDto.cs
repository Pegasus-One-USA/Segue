namespace FHIRBridge.Application.DTOs;

/// <summary>
/// One element/field in the FHIR R4 catalog (generated from StructureDefinition snapshots).
/// Read-only reference data served to the mapping UI.
/// </summary>
public sealed record FhirElementDto(
    string Label,
    string JsonPath,
    string FhirPath,
    string Cardinality,
    string ValueType,
    bool IsArray,
    IReadOnlyList<string> Arrays);
