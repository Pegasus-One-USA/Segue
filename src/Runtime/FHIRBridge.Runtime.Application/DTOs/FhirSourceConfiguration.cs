using FHIRBridge.Runtime.Domain.Enums;

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
    string? AuthorizationEndpoint = null);
