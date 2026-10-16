using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Services;

public interface IClientCredentialsTokenService
{
    /// <summary>Validates clientId/clientSecret (OAuth 2.0 Client Credentials Grant, RFC 6749 §4.4) and, on
    /// success, mints a short-lived Bearer token and stamps the client's LastUsedOnUtc. Every attempt is
    /// audited via IGovernanceLogger, success and failure alike, without ever logging the secret.</summary>
    Task<ClientCredentialsTokenResult> IssueTokenAsync(
        string clientId, string clientSecret, CancellationToken cancellationToken);
}
