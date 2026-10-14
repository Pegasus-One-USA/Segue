using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using Microsoft.IdentityModel.Tokens;

namespace FHIRBridge.Api.Security;

/// <summary>
/// OAuth 2.0 Client Credentials Grant (RFC 6749 §4.4) token issuer — signs with the same key/algorithm as
/// <see cref="JwtAccessTokenIssuer"/> (so it validates through the same "Local" bearer scheme with no auth
/// pipeline changes, see <see cref="FhirBridgeAuthenticationExtensions"/>), but carries no "uid" claim. That
/// absence is exactly what keeps a client-credentials token from satisfying any <c>HasPermission:*</c>-gated
/// endpoint (see <see cref="PermissionAuthorizationHandler"/>) — it only ever works where explicitly taught
/// to recognize the <c>token_use</c> claim below (see <c>WorkflowEndpoints</c>'s <c>/run</c> handler).
/// </summary>
public sealed class ClientCredentialsAccessTokenIssuer : IClientCredentialsAccessTokenIssuer
{
    private readonly IConfiguration _configuration;
    private readonly IAppSecretAccessor _secretAccessor;

    public ClientCredentialsAccessTokenIssuer(IConfiguration configuration, IAppSecretAccessor secretAccessor)
    {
        _configuration = configuration;
        _secretAccessor = secretAccessor;
    }

    public AccessTokenDto Issue(ApiClient client)
    {
        var signingKey = _secretAccessor.JwtSigningKey;
        if (string.IsNullOrWhiteSpace(signingKey))
        {
            throw new InvalidOperationException("The JWT signing key has not been provisioned yet.");
        }

        // Deliberately shorter-lived than the 60-minute human login token (default 15 min) — short-lived
        // machine tokens are the OAuth2 best practice, and RFC 6749 §4.4.3 says a refresh token SHOULD NOT be
        // issued for this grant: the client just re-authenticates with client_id/secret for a new one.
        var lifetimeMinutes = int.TryParse(
            _configuration["Authentication:ClientCredentialsTokenLifetimeMinutes"], out var minutes)
            ? minutes
            : 15;
        var expiresOnUtc = DateTime.UtcNow.AddMinutes(lifetimeMinutes);

        var claims = new List<Claim>
        {
            new("client_id", client.ClientId),
            new("token_use", "client_credentials"),
            new("scope", "workflow.trigger"),
        };

        var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey));
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(claims: claims, expires: expiresOnUtc, signingCredentials: credentials);

        return new AccessTokenDto(new JwtSecurityTokenHandler().WriteToken(token), "Bearer", expiresOnUtc);
    }
}
