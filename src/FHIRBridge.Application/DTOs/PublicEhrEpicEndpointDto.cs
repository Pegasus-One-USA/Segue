namespace FHIRBridge.Application.DTOs;

/// <summary>
/// Anonymous, minimal-data view of an EhrEndpoint row tagged EndpointType.Epic (the vendor's own public test
/// sandbox, not a specific customer's MyChart production instance) — for third-party apps building an
/// organization/hospital picker ahead of an unauthenticated SMART Standalone Launch. Deliberately narrower than
/// EhrEndpointDto: no VendorEndpointId, no audit fields, nothing that isn't already safe to hand to an anonymous
/// caller.
/// </summary>
public sealed record PublicEhrEpicEndpointDto(Guid Id, string Name, string FhirBaseUrl, string Status);
