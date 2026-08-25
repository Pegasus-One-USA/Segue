namespace FHIRBridge.Application.DTOs;

/// <summary>
/// Ad-hoc Medplum connection test (the destination wizard / New Connection dialog's Test Connection button,
/// before anything is saved). Credentials come straight off this request — never a stored secret, since
/// nothing has been persisted yet. Mirrors <see cref="FhirConnectionTestRequest"/>'s "test before save" shape;
/// the result reuses the shared <see cref="ConnectionTestResultDto"/> (Connected + Error).
/// </summary>
public sealed record MedplumConnectionTestRequest(
    string BaseUrl,
    string ClientId,
    /// <summary>The client secret, or the PEM private key when <see cref="AuthMethod"/> is "private_key_jwt".</summary>
    string Secret,
    /// <summary>"client_secret" (default) or "private_key_jwt".</summary>
    string? AuthMethod,
    /// <summary>The JWT header <c>kid</c> for the private_key_jwt flow; ignored for client_secret.</summary>
    string? KeyId,
    /// <summary>Optional token-endpoint override; derived from <see cref="BaseUrl"/> when omitted.</summary>
    string? TokenUrl);
