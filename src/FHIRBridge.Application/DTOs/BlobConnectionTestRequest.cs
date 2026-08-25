namespace FHIRBridge.Application.DTOs;

/// <summary>
/// Ad-hoc Azure Blob Storage connection test (the destination form's Test Connection button, before anything is
/// saved). Carries the already-split auth fields the form collects rather than a metadata JSON blob, so the test
/// service can build the client directly without constructing a <see cref="Domain.Entities.DestinationConfiguration"/>.
/// <paramref name="Secret"/> is the connection string / account key / SAS / service-principal client secret per
/// <paramref name="AuthMode"/>; it is null for Managed Identity (which resolves no secret). The result reuses the
/// shared <see cref="ConnectionTestResultDto"/> (Connected + Error).
/// </summary>
public sealed record BlobConnectionTestRequest(
    string AuthMode,
    string Container,
    string? Secret,
    string? AccountUrl,
    string? AccountName,
    string? EndpointSuffix,
    string? TenantId,
    string? ClientId,
    string? ManagedIdentityClientId);
