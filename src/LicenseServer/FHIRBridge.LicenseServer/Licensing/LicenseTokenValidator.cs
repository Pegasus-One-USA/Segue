using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace FHIRBridge.LicenseServer.Licensing;

/// <summary>Result of verifying a license token's signature — just enough to drive the check-in endpoint
/// and, if wanted later, a "verify a license" admin page. Never throws; see <see cref="LicenseTokenValidator"/>.</summary>
public sealed record LicenseVerificationResult(bool IsValid, string? Sub, string? CustomerName, string? Edition, string? InvalidReason)
{
    public static LicenseVerificationResult Invalid(string reason) => new(false, null, null, null, reason);
}

/// <summary>
/// Verifies a compact-JWS license token's ES256 signature against this server's own active key (see
/// <see cref="LicenseSigningKeyProvider"/>) and, on success, pulls out the handful of claims the check-in
/// endpoint needs. Mirrors the signature/issuer verification approach in the main FHIRBridge repo's
/// <c>SignedLicenseValidator</c> — same <see cref="JsonWebTokenHandler"/> +
/// <see cref="TokenValidationParameters"/> shape, same "never throw, come back Invalid instead" contract —
/// but does not need that class's full claim-to-LicenseStatus parsing (limits, hospitals, etc.); this
/// server only needs to know WHO the token belongs to, since counts/limits enforcement is the deployed
/// FHIRBridge instance's own job, not this server's.
/// </summary>
public sealed class LicenseTokenValidator
{
    private const string ExpectedIssuer = "pegasusone";

    private readonly LicenseSigningKeyProvider _keyProvider;

    public LicenseTokenValidator(LicenseSigningKeyProvider keyProvider)
    {
        _keyProvider = keyProvider;
    }

    public LicenseVerificationResult Validate(string? licenseToken)
    {
        if (string.IsNullOrWhiteSpace(licenseToken))
        {
            return LicenseVerificationResult.Invalid("No license token was provided.");
        }

        var validationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new ECDsaSecurityKey(_keyProvider.Key),
            ValidAlgorithms = new[] { SecurityAlgorithms.EcdsaSha256 },
            ValidateIssuer = true,
            ValidIssuer = ExpectedIssuer,
            ValidateAudience = false,
            // Signature validity is all this endpoint cares about — an expired-but-genuinely-signed license
            // still reports usage; the deployed instance (and a human looking at the dashboard) can see the
            // expiry for themselves.
            ValidateLifetime = false,
            RequireExpirationTime = false,
            RequireSignedTokens = true,
        };

        TokenValidationResult result;
        try
        {
            result = new JsonWebTokenHandler()
                .ValidateTokenAsync(licenseToken, validationParameters)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            return LicenseVerificationResult.Invalid($"License token could not be parsed: {Truncate(ex.Message)}");
        }

        if (!result.IsValid || result.SecurityToken is not JsonWebToken jsonWebToken)
        {
            var reason = result.Exception?.Message ?? "Signature or issuer validation failed.";
            return LicenseVerificationResult.Invalid(Truncate(reason));
        }

        var sub = jsonWebToken.TryGetPayloadValue<string>("sub", out var subValue) ? subValue : null;
        var customerName = jsonWebToken.TryGetPayloadValue<string>("customerName", out var nameValue) ? nameValue : null;
        var edition = jsonWebToken.TryGetPayloadValue<string>("edition", out var editionValue) ? editionValue : null;

        return new LicenseVerificationResult(true, sub, customerName, edition, null);
    }

    private static string Truncate(string value) => value.Length > 200 ? value[..200] : value;
}
