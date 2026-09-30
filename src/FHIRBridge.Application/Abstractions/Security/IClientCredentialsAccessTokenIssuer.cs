using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Application.Abstractions.Security;

/// <summary>Mints a short-lived Bearer token for a validated <see cref="ApiClient"/> — the OAuth 2.0 Client
/// Credentials Grant (RFC 6749 §4.4) counterpart of <see cref="IAccessTokenIssuer"/>'s user-login tokens.
/// Deliberately a separate issuer (not a new overload on IAccessTokenIssuer): a client-credentials token
/// carries no "uid" claim and is never eligible for a refresh token (RFC 6749 §4.4.3), so its shape and
/// lifecycle differ enough from a human login token to warrant its own contract.</summary>
public interface IClientCredentialsAccessTokenIssuer
{
    AccessTokenDto Issue(ApiClient client);
}
