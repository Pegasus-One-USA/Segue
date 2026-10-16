using System.Security.Cryptography;
using System.Text;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace FHIRBridge.Infrastructure.Cds;

/// <summary>The outcome of checking a CDS client's JWT.</summary>
/// <param name="Issuer">The token's iss when it could be read (stored on the call log; never the token itself).</param>
/// <param name="TrustedIssuer">The matching enabled trusted issuer, once found.</param>
/// <param name="Failure">A short, non-PHI reason when <paramref name="IsValid"/> is false.</param>
public sealed record CdsJwtValidationResult(bool IsValid, string? Issuer, CdsTrustedIssuer? TrustedIssuer, string? Failure)
{
    public static CdsJwtValidationResult Fail(string? issuer, CdsTrustedIssuer? trustedIssuer, string failure) =>
        new(false, issuer, trustedIssuer, failure);
}

/// <summary>
/// Validates the bearer JWT a CDS client (EHR) signs for each call, per CDS Hooks 2.0: iss must be an enabled
/// <see cref="CdsTrustedIssuer"/>; the signature must verify (RS256/RS384/ES256/ES384 only) against that issuer's
/// JWKS URL keys (cached, refetched once on an unknown kid) or pasted key set; aud must be the exact URL called
/// (or the issuer's audience override); exp is required and checked with a small clock skew; and each jti is
/// accepted once per issuer (replay protection in IDistributedCache until the token expires). A header jku must be
/// the issuer's configured JWKS URL (keys are never fetched from the token's jku), and sub must equal the issuer's
/// expected subject when one is set.
/// </summary>
public sealed class CdsJwtValidator
{
    public static readonly IReadOnlyList<string> AllowedAlgorithms =
    [
        SecurityAlgorithms.RsaSha256,
        SecurityAlgorithms.RsaSha384,
        SecurityAlgorithms.EcdsaSha256,
        SecurityAlgorithms.EcdsaSha384,
    ];

    private readonly ICdsTrustedIssuerRepository _issuers;
    private readonly CdsJwksProvider _jwks;
    private readonly CdsReplayGuard _replayGuard;
    private readonly IOptionsMonitor<CdsOptions> _options;
    private readonly JsonWebTokenHandler _handler = new() { MapInboundClaims = false };

    public CdsJwtValidator(
        ICdsTrustedIssuerRepository issuers,
        CdsJwksProvider jwks,
        CdsReplayGuard replayGuard,
        IOptionsMonitor<CdsOptions> options)
    {
        _issuers = issuers;
        _jwks = jwks;
        _replayGuard = replayGuard;
        _options = options;
    }

    /// <param name="authorizationHeader">The raw Authorization header. Never logged.</param>
    /// <param name="expectedAudiences">
    /// The URL(s) the client called, built from the configured Cds:PublicBaseUrl; replaced by the issuer's audience
    /// override when set. Empty when no public base URL is configured: then only an issuer with an override passes.
    /// </param>
    public async Task<CdsJwtValidationResult> ValidateAsync(
        string? authorizationHeader,
        IReadOnlyCollection<string> expectedAudiences,
        CancellationToken cancellationToken)
    {
        var token = ExtractBearerToken(authorizationHeader);
        if (token is null)
        {
            return CdsJwtValidationResult.Fail(null, null, "No bearer token was sent.");
        }

        JsonWebToken jwt;
        try
        {
            jwt = _handler.ReadJsonWebToken(token);
        }
        catch (Exception exception) when (exception is ArgumentException or SecurityTokenMalformedException)
        {
            return CdsJwtValidationResult.Fail(null, null, "The bearer token is not a well-formed JWT.");
        }

        var issuer = string.IsNullOrWhiteSpace(jwt.Issuer) ? null : jwt.Issuer;
        if (issuer is null)
        {
            return CdsJwtValidationResult.Fail(null, null, "The token has no iss claim.");
        }

        if (!AllowedAlgorithms.Contains(jwt.Alg, StringComparer.Ordinal))
        {
            return CdsJwtValidationResult.Fail(issuer, null, $"Signing algorithm '{Truncate(jwt.Alg, 20)}' is not allowed (RS256, RS384, ES256 or ES384).");
        }

        var trusted = await _issuers.GetEnabledByIssuerAsync(issuer, cancellationToken);
        if (trusted is null)
        {
            return CdsJwtValidationResult.Fail(issuer, null, "The issuer is not an enabled trusted CDS client.");
        }

        var audiences = trusted.Audience is { } audienceOverride ? [audienceOverride] : expectedAudiences;
        if (audiences.Count == 0)
        {
            // Fail closed: an audience derived from the caller's own Host header would bind the token to nothing.
            return CdsJwtValidationResult.Fail(issuer, trusted,
                "Cds:PublicBaseUrl is not set, so the expected aud is unknown. Set it (or the issuer's audience override).");
        }

        // Epic puts its JWK Set URL in the header (jku). Keys come only from the issuer's configured JWKS URL, never
        // from the token; a jku naming any other URL is refused rather than ignored.
        if (jwt.TryGetHeaderValue<string>("jku", out var jku) && !string.IsNullOrWhiteSpace(jku) && !trusted.IsTrustedJku(jku))
        {
            return CdsJwtValidationResult.Fail(issuer, trusted,
                trusted.JwksUrl is null
                    ? "The token names a jku, but this issuer has no JWKS URL configured to compare it with."
                    : "The token's jku is not this issuer's configured JWKS URL.");
        }

        var options = _options.CurrentValue;
        var keys = await GetKeysAsync(trusted, jwt.Kid, cancellationToken);
        if (keys.Count == 0)
        {
            return CdsJwtValidationResult.Fail(issuer, trusted, "No usable signing keys are available for this issuer (check its JWKS).");
        }

        var result = await _handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = trusted.Issuer,
            ValidateAudience = true,
            AudienceValidator = (tokenAudiences, _, _) =>
                tokenAudiences.Any(audience => audiences.Any(expected => SameUrl(audience, expected))),
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromSeconds(Math.Max(0, options.ClockSkewSeconds)),
            RequireSignedTokens = true,
            IssuerSigningKeys = keys,
            TryAllIssuerSigningKeys = true,
            ValidAlgorithms = AllowedAlgorithms,
        });

        if (!result.IsValid)
        {
            return CdsJwtValidationResult.Fail(issuer, trusted, Describe(result.Exception, jwt, audiences));
        }

        if (jwt.ValidTo > DateTime.UtcNow.AddMinutes(Math.Max(1, options.MaxTokenLifetimeMinutes)))
        {
            return CdsJwtValidationResult.Fail(issuer, trusted, $"The token's exp is more than {options.MaxTokenLifetimeMinutes} minutes away.");
        }

        // Epic: sub is the health system's CDS client id. Checked only when the issuer names one.
        if (trusted.ExpectedSubject is { } expectedSubject && !string.Equals(jwt.Subject, expectedSubject, StringComparison.Ordinal))
        {
            return CdsJwtValidationResult.Fail(issuer, trusted, "The token's sub is not this issuer's expected client id.");
        }

        if (string.IsNullOrWhiteSpace(jwt.Id))
        {
            return CdsJwtValidationResult.Fail(issuer, trusted, "The token has no jti claim.");
        }

        // Atomic set-if-absent: of two concurrent requests carrying the same token, only one is accepted.
        var firstUse = await _replayGuard.TryMarkUsedAsync(
            ReplayKey(issuer, jwt.Id),
            new DateTimeOffset(jwt.ValidTo, TimeSpan.Zero).AddSeconds(Math.Max(0, options.ClockSkewSeconds)),
            cancellationToken);
        if (!firstUse)
        {
            return CdsJwtValidationResult.Fail(issuer, trusted, "The token was already used (jti replay).");
        }

        return new CdsJwtValidationResult(true, issuer, trusted, null);
    }

    private async Task<IReadOnlyList<SecurityKey>> GetKeysAsync(CdsTrustedIssuer trusted, string? kid, CancellationToken cancellationToken)
    {
        var keys = new List<SecurityKey>(CdsJwksProvider.ParseKeys(trusted.JwksJson));
        if (trusted.JwksUrl is { } jwksUrl)
        {
            var fetched = await _jwks.GetKeysAsync(jwksUrl, refresh: false, cancellationToken);
            if (!string.IsNullOrEmpty(kid) && !fetched.Concat(keys).Any(key => key.KeyId == kid))
            {
                // Unknown kid: the issuer may have rotated its keys since the set was cached.
                fetched = await _jwks.GetKeysAsync(jwksUrl, refresh: true, cancellationToken);
            }

            keys.AddRange(fetched);
        }

        return keys;
    }

    private static string Describe(Exception? exception, JsonWebToken jwt, IReadOnlyCollection<string> audiences) => exception switch
    {
        SecurityTokenExpiredException => "The token has expired.",
        SecurityTokenNotYetValidException => "The token is not valid yet (nbf).",
        SecurityTokenNoExpirationException => "The token has no exp claim.",
        SecurityTokenInvalidAudienceException =>
            $"The token's aud ({Truncate(string.Join(", ", jwt.Audiences), 150)}) does not match the expected " +
            $"{Truncate(string.Join(" or ", audiences), 200)}. Check Cds:PublicBaseUrl or the issuer's audience override.",
        SecurityTokenInvalidIssuerException => "The token's iss does not match the trusted issuer.",
        SecurityTokenSignatureKeyNotFoundException => "No signing key matching the token's kid was found for this issuer.",
        SecurityTokenInvalidSignatureException => "The token's signature did not verify.",
        SecurityTokenInvalidAlgorithmException => "The token's signing algorithm is not allowed.",
        _ => "The token is invalid.",
    };

    private static string? ExtractBearerToken(string? header)
    {
        if (string.IsNullOrWhiteSpace(header) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = header["Bearer ".Length..].Trim();
        return token.Length == 0 ? null : token;
    }

    private static bool SameUrl(string? left, string? right) =>
        left is not null && right is not null
        && string.Equals(left.Trim().TrimEnd('/'), right.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private static string ReplayKey(string issuer, string jti) =>
        "cds:jti:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{issuer}\n{jti}")));

    private static string Truncate(string? value, int maxLength) =>
        value is null ? string.Empty : value.Length > maxLength ? value[..maxLength] + "…" : value;
}
