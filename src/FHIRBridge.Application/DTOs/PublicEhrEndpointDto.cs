namespace FHIRBridge.Application.DTOs;

/// <summary>
/// Anonymous, minimal-data view of an EhrEndpoint row — regardless of EndpointType (the vendor's shared test
/// sandbox and a specific customer's own MyChart production instance are both included) — for third-party apps
/// building an organization/hospital picker ahead of an unauthenticated SMART Standalone Launch. Deliberately
/// narrower than EhrEndpointDto: no VendorEndpointId, no audit fields, nothing that isn't already safe to hand to
/// an anonymous caller.
/// </summary>
public sealed record PublicEhrEndpointDto(Guid Id, string Name, string FhirBaseUrl, string Status);
