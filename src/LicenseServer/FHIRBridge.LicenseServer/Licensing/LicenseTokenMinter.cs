using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace FHIRBridge.LicenseServer.Licensing;

/// <summary>
/// Signs <see cref="LicenseFields"/> into a compact-JWS license token. Claim shape and signing approach
/// (ES256 via <see cref="JsonWebTokenHandler"/> + <see cref="ECDsaSecurityKey"/>) are copied verbatim from
/// the main FHIRBridge repo's <c>tools/FHIRBridge.LicenseMinter/Program.cs</c> so every token minted here
/// verifies against that repo's <c>SignedLicenseValidator</c> unchanged.
/// </summary>
public sealed class LicenseTokenMinter
{
    private readonly LicenseSigningKeyProvider _keyProvider;

    public LicenseTokenMinter(LicenseSigningKeyProvider keyProvider)
    {
        _keyProvider = keyProvider;
    }

    /// <summary>Mints a signed token plus the exact claims JSON that was signed (for the audit trail).</summary>
    public (string Token, string ClaimsJson) Mint(LicenseFields fields, DateTime nowUtc)
    {
        var claimsJson = BuildClaimsJson(fields, nowUtc);

        var credentials = new SigningCredentials(new ECDsaSecurityKey(_keyProvider.Key), SecurityAlgorithms.EcdsaSha256);
        var token = new JsonWebTokenHandler().CreateToken(claimsJson, credentials);

        return (token, claimsJson);
    }

    private static string BuildClaimsJson(LicenseFields fields, DateTime nowUtc)
    {
        object? allowedHospitals = fields.AllowedHospitals is { Count: > 0 }
            ? fields.AllowedHospitals
                .Select(h => new { vendor = h.Vendor, baseUrl = h.BaseUrl, displayName = h.DisplayName })
                .ToArray()
            : null;

        object? allowedSourceTypes = fields.AllowedSourceTypes is { Count: > 0 }
            ? fields.AllowedSourceTypes.ToArray()
            : null;

        object? allowedResourceTypes = fields.AllowedResourceTypes is { Count: > 0 }
            ? fields.AllowedResourceTypes.ToArray()
            : null;

        object? allowedDestinationTypes = fields.AllowedDestinationTypes is { Count: > 0 }
            ? fields.AllowedDestinationTypes.ToArray()
            : null;

        return JsonSerializer.Serialize(new
        {
            iss = "pegasusone",
            sub = fields.CustomerId,
            customerName = fields.CustomerName,
            edition = fields.Edition,
            nbf = ToUnixSeconds(nowUtc),
            exp = ToUnixSeconds(fields.ExpiresUtc),
            maxUsers = fields.MaxUsers,
            maxWorkflows = fields.MaxWorkflows,
            maxSourceConnections = fields.MaxSourceConnections,
            features = fields.Features,
            allowedSourceTypes,
            allowedHospitals,
            maxProcessedRecordsPerMonth = fields.MaxProcessedRecordsPerMonth,
            allowedResourceTypes,
            allowedDestinationTypes,
            maxSuccessfulWorkflowExecutionsPerMonth = fields.MaxSuccessfulWorkflowExecutionsPerMonth,
            // Absent when ActivationWindowMinutes is null — SignedLicenseValidator only enforces this claim
            // when present, so an unset window means "no activation deadline".
            activateByUtc = fields.ActivationWindowMinutes.HasValue
                ? ToUnixSeconds(nowUtc.AddMinutes(fields.ActivationWindowMinutes.Value))
                : (long?)null,
            // Absent when RequestKey is null — SignedLicenseValidator only enforces the UniqueKey match at
            // apply time when both this claim AND a local LicenseRequest row are present.
            requestKey = fields.RequestKey,
        });
    }

    private static long ToUnixSeconds(DateTime value) =>
        new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)).ToUnixTimeSeconds();
}
