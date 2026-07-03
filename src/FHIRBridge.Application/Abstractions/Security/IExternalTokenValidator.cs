using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.Abstractions.Security;

/// <summary>
/// A verified identity extracted from an external IdP token after signature/issuer/audience validation.
/// </summary>
/// <param name="Provider">Which provider issued the token (Entra or Google).</param>
/// <param name="Subject">The provider's stable user identifier (Entra oid/sub, Google sub).</param>
/// <param name="Email">The verified email / preferred username from the token.</param>
/// <param name="Name">Optional display name.</param>
public sealed record ExternalIdentity(
    LoginProvider Provider,
    string Subject,
    string Email,
    string? Name);

/// <summary>
/// Validates an external IdP bearer token (Entra or Google) and returns the verified identity. Used by
/// the SSO token-exchange endpoints: validate an external token, then mint a FHIRBridge-local JWT.
/// Implementations must throw <see cref="InvalidOperationException"/> for invalid/expired tokens or a
/// disabled provider.
/// </summary>
public interface IExternalTokenValidator
{
    Task<ExternalIdentity> ValidateAsync(LoginProvider provider, string token, CancellationToken cancellationToken);
}
