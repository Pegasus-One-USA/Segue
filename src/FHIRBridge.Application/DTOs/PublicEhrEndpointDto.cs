namespace FHIRBridge.Application.DTOs;

/// <summary>
/// Lean, unauthenticated-safe projection of an EhrEndpoint for public-facing pickers (e.g. a demo/patient app's
/// "choose your hospital" list) — deliberately excludes everything the admin-only EhrEndpointDto carries
/// (FhirBaseUrl, audit fields, vendor internals).
/// </summary>
public sealed record PublicEhrEndpointDto(Guid Id, string Name);
