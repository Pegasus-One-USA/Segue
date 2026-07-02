using FHIRBridge.Runtime.Domain.Enums;
using FHIRBridge.SharedKernel.Enums;

namespace FHIRBridge.Runtime.Application.DTOs;

public sealed record FhirSourceConfiguration(
    RuntimeSourceType SourceType,
    string? Name,
    string? BaseUrl,
    string? TokenEndpoint,
    string? ClientId,
    string? KeyId,
    string? PrivateKeyPem,
    IReadOnlyCollection<string> Scopes,
    int SearchCount = 100,
    int MaxPages = 5,
    Guid? TenantId = null,
    Guid? SourceConnectionId = null,
    string? SearchParameters = null,
    string? ClientSecret = null,
    string? AuthorizationEndpoint = null,
    // Application-type (composition) axis: when set it selects the SMART grant/launch flow independently of the
    // vendor. Left null for backward compatibility — the composite token provider then infers the grant from the
    // vendor and the credentials present.
    ApplicationType? ApplicationType = null,
    // Pass-through (provider-standalone) axis: a token the CALLER already acquired from its own SMART login. When
    // set, the composite token provider returns it verbatim and performs NO OAuth. Null for every existing flow, so
    // their token acquisition is completely unchanged.
    string? AccessToken = null);
