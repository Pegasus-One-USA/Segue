using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using FHIRBridge.Application.Abstractions.Persistence;
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
        // issued for this grant: the client just re-authenticates with client_id/secret for a new one. Clamped:
        // 0 or less issued an already-expired token with a 200, and a huge value made AddMinutes throw (a 500).
        var lifetimeMinutes = int.TryParse(
            _configuration["Authentication:ClientCredentialsTokenLifetimeMinutes"], out var minutes)
            && minutes is >= MinLifetimeMinutes and <= MaxLifetimeMinutes
            ? minutes
            : DefaultLifetimeMinutes;
        var issuedOnUtc = DateTime.UtcNow;
        var expiresOnUtc = issuedOnUtc.AddMinutes(lifetimeMinutes);

        var claims = new List<Claim>
        {
            new("client_id", client.ClientId),
            new("token_use", "client_credentials"),
            new("scope", "workflow.trigger"),
            new(SecretFingerprintClaim, SecretFingerprint(client)),
        };

        var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey));
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            claims: claims, notBefore: issuedOnUtc, expires: expiresOnUtc, signingCredentials: credentials);

        return new AccessTokenDto(new JwtSecurityTokenHandler().WriteToken(token), "Bearer", expiresOnUtc);
    }

    /// <summary>
    /// Whether a client-credentials token still belongs to a usable API client: the client still exists, is enabled,
    /// and its secret hasn't been regenerated since the token was issued. The token is self-contained, so without
    /// this check disabling, deleting or rotating a client left every token it had issued working until expiry.
    /// A token minted before this claim existed (no fingerprint) is refused — it is short-lived anyway.
    /// </summary>
    public static async Task<bool> IsStillValidAsync(
        ClaimsPrincipal user, IApiClientRepository apiClients, CancellationToken cancellationToken)
    {
        var clientId = user.FindFirst("client_id")?.Value;
        var fingerprint = user.FindFirst(SecretFingerprintClaim)?.Value;
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(fingerprint))
        {
            return false;
        }

        var client = await apiClients.GetByClientIdAsync(clientId, cancellationToken);
        return client is { IsEnabled: true }
            && CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(fingerprint), Encoding.ASCII.GetBytes(SecretFingerprint(client)));
    }

    /// <summary>
    /// A short digest of the client's stored secret HASH (never the secret itself). Regenerating the secret replaces
    /// the hash, so it changes this value and every token carrying the old one stops validating — revocation without
    /// a "rotated on" column.
    /// </summary>
    private static string SecretFingerprint(ApiClient client) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(client.ClientSecretHash)))[..16];

    private const string SecretFingerprintClaim = "sfp";
    private const int DefaultLifetimeMinutes = 15;
    private const int MinLifetimeMinutes = 1;
    private const int MaxLifetimeMinutes = 60;
}
