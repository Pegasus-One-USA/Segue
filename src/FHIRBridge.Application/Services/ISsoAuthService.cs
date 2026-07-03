using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Services;

/// <summary>
/// SSO token exchange: validate an external IdP (Entra/Google) token and mint a FHIRBridge-local session
/// for a known, enabled user. Permissions continue to flow through the FHIRBridge-minted JWT unchanged.
/// </summary>
public interface ISsoAuthService
{
    /// <summary>
    /// Validates the external token, resolves the matching user (by external subject, else by verified email),
    /// links the external identity if not already linked, and returns a signed-in session. Throws
    /// <see cref="UnauthorizedAccessException"/> when no enabled matching user exists.
    /// </summary>
    Task<LocalLoginResponse> LoginAsync(SsoLoginRequest request, CancellationToken cancellationToken);
}
