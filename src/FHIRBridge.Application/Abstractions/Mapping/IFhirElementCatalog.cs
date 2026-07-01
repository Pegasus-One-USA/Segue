using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Abstractions.Mapping;

/// <summary>
/// Read-only catalog of FHIR R4 resource types and their fields (cardinality, value type,
/// array ancestors), generated from StructureDefinition snapshots. Static reference data.
/// </summary>
public interface IFhirElementCatalog
{
    string FhirVersion { get; }

    IReadOnlyList<string> ResourceTypes { get; }

    IReadOnlyList<FhirElementDto> Fields(string resourceType);
}
