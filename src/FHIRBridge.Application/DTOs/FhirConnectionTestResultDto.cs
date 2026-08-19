namespace FHIRBridge.Application.DTOs;

/// <summary>
/// Result of an ad-hoc FHIR-repository connection test. A separate type from the shared
/// <see cref="ConnectionTestResultDto"/> (used by SFTP) rather than widening it, since
/// <c>ResolvedTokenEndpoint</c> is meaningful only for FHIR's "clientcredentials"/"oauth2" auth type — the wizard
/// patches it into the (now-hidden) tokenEndpoint form control on success so it still ends up in
/// dest_tokenEndpoint/the secret blob at save time, exactly as a manually-typed value would have.
/// </summary>
public sealed record FhirConnectionTestResultDto(bool Connected, string? Error, string? ResolvedTokenEndpoint);
