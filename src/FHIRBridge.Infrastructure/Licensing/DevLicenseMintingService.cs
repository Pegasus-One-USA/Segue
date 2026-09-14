using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace FHIRBridge.Infrastructure.Licensing;

/// <summary>
/// ⚠ TEMPORARY / DEV-ONLY implementation of <see cref="IDevLicenseMintingService"/> — signs a license
/// token with <see cref="DevLicenseSigningKey"/>'s throwaway private key, producing exactly the claims
/// shape <c>SignedLicenseValidator</c> (and <c>tools/FHIRBridge.LicenseMinter</c>) already agree on: `iss`,
/// `sub`, `customerName`, `edition`, `nbf`, `exp`, `maxUsers`, `maxWorkflows`, `maxSourceConnections`,
/// `features`, `allowedSourceTypes`, `allowedHospitals`, `maxProcessedRecordsPerMonth` (`-1` = unlimited
/// for every numeric field), `allowedResourceTypes`, `allowedDestinationTypes`.
/// Exists ONLY to back <c>DevLicenseMintingController</c>'s temporary minting
/// endpoint for the portal's throwaway "Dev: Mint a test license" page.
///
/// DELETE THIS FILE alongside <see cref="DevLicenseSigningKey"/>, <see cref="IDevLicenseMintingService"/>,
/// and <c>DevLicenseMintingController</c> once license minting moves to its own separate internal tool.
///
/// The signing <see cref="ECDsa"/> is imported once and kept alive for the process's lifetime rather than
/// created-and-disposed per call — same reasoning as <c>SignedLicenseValidator.PublicKey</c>'s remarks:
/// Microsoft.IdentityModel.Tokens' process-wide <c>CryptoProviderFactory.Default</c> caches
/// <c>SignatureProvider</c>s keyed by key material, and disposing a key right after using it has been
/// observed (see <c>SignedLicenseValidatorTests</c>' repeated "deliberately not disposed" comments on the
/// verify side) to hand a LATER call using the same key material a stale provider wrapping an already-
/// disposed key. Keeping this one instance alive for the whole process avoids that entirely.
/// </summary>
public sealed class DevLicenseMintingService : IDevLicenseMintingService
{
    /// <summary>The only issuer <c>SignedLicenseValidator</c> ever accepts — see its <c>ExpectedIssuer</c>.</summary>
    private const string Issuer = "pegasusone";

    private static readonly Lazy<ECDsa> SigningKey = new(() =>
    {
        var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(DevLicenseSigningKey.DevPrivateKeyPkcs8Base64), out _);
        return ecdsa;
    });

    public string Mint(DevLicenseMintRequest request)
    {
        var customerName = string.IsNullOrWhiteSpace(request.CustomerName) ? request.CustomerId : request.CustomerName;
        var edition = string.IsNullOrWhiteSpace(request.Edition) ? "standard" : request.Edition;
        var features = request.Features is { Count: > 0 } ? request.Features : Array.Empty<string>();
        var allowedSourceTypes = request.AllowedSourceTypes is { Count: > 0 } ? request.AllowedSourceTypes : null;
        var allowedHospitals = request.AllowedHospitals is { Count: > 0 }
            ? request.AllowedHospitals
                .Select(h => new { vendor = h.Vendor, baseUrl = h.BaseUrl, displayName = h.DisplayName })
                .ToArray()
            : null;
        var allowedResourceTypes = request.AllowedResourceTypes is { Count: > 0 } ? request.AllowedResourceTypes : null;
        var allowedDestinationTypes = request.AllowedDestinationTypes is { Count: > 0 } ? request.AllowedDestinationTypes : null;

        var payloadJson = JsonSerializer.Serialize(new
        {
            iss = Issuer,
            sub = request.CustomerId,
            customerName,
            edition,
            nbf = ToUnixSeconds(DateTime.UtcNow),
            exp = ToUnixSeconds(request.ExpiresUtc),
            maxUsers = request.MaxUsers,
            maxWorkflows = request.MaxWorkflows,
            maxSourceConnections = request.MaxSourceConnections,
            features,
            allowedSourceTypes,
            allowedHospitals,
            maxProcessedRecordsPerMonth = request.MaxProcessedRecordsPerMonth,
            allowedResourceTypes,
            allowedDestinationTypes,
            maxSuccessfulWorkflowExecutionsPerMonth = request.MaxSuccessfulWorkflowExecutionsPerMonth,
        });

        var credentials = new SigningCredentials(new ECDsaSecurityKey(SigningKey.Value), SecurityAlgorithms.EcdsaSha256);
        return new JsonWebTokenHandler().CreateToken(payloadJson, credentials);
    }

    private static long ToUnixSeconds(DateTime value) =>
        new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)).ToUnixTimeSeconds();
}
