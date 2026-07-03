namespace FHIRBridge.Application.DTOs;

public sealed record SourceCapabilityProfileDto(
    Guid SourceConnectionId,
    string FhirVersion,
    DateTime DiscoveredOnUtc,
    IReadOnlyList<string> ConfiguredScopes,
    IReadOnlyList<CapabilityResourceDto> Resources);

public sealed record CapabilityResourceDto(
    string ResourceType,
    IReadOnlyList<string> Interactions);

/// <summary>
/// A FHIR resource type from the element catalog, annotated with whether the selected source can provide it.
/// Drives the resource-type dropdown gating in the mapping editor UI.
/// </summary>
public sealed record CatalogResourceAvailabilityDto(
    string ResourceType,
    bool Available,
    string? Reason);
