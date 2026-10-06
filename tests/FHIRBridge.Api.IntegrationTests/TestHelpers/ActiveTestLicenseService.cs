using System.Text.Json;
using FHIRBridge.Application.Abstractions.Licensing;
using Microsoft.IdentityModel.JsonWebTokens;

namespace FHIRBridge.Api.IntegrationTests.TestHelpers;

/// <summary>
/// The license service of the integration-test host. It starts Active and unlimited, because the API's license gate
/// blocks every /api/v1 call while no license is Active, and a real license must be signed with the licensor's
/// production private key, which no test has (the verifying public key is compiled in; see LicensePublicKey).
///
/// <para>Applying a license through <c>POST /api/v1/license</c> adopts that token's limits (maxUsers,
/// maxWorkflows, maxSourceConnections, allowedSourceTypes) WITHOUT checking its signature, so the real quota guards
/// enforce them exactly as they would a verified license. Signature verification itself is covered by
/// SignedLicenseValidatorTests in the unit tests.</para>
/// </summary>
public sealed class ActiveTestLicenseService : ILicenseService
{
    private static readonly LicenseStatus Unlimited = Status(new LicenseLimits());

    private volatile LicenseStatus _current = Unlimited;

    public LicenseStatus Current => _current;

    public string? CurrentRawToken { get; private set; }

    public bool HasFeature(string featureKey) => true;

    public Task<LicenseApplyResult> ApplyAsync(string licenseToken, CancellationToken cancellationToken)
    {
        LicenseLimits limits;
        try
        {
            var token = new JsonWebToken(licenseToken);
            limits = new LicenseLimits(
                MaxUsers: Int(token, "maxUsers"),
                MaxWorkflows: Int(token, "maxWorkflows"),
                MaxSourceConnections: Int(token, "maxSourceConnections"),
                AllowedSourceTypes: Strings(token, "allowedSourceTypes"));
        }
        catch (Exception ex) when (ex is ArgumentException or Microsoft.IdentityModel.Tokens.SecurityTokenMalformedException)
        {
            return Task.FromResult(new LicenseApplyResult(false, "The license token could not be read.", null));
        }

        _current = Status(limits);
        CurrentRawToken = licenseToken;
        return Task.FromResult(new LicenseApplyResult(true, null, _current));
    }

    public Task ReloadAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task ClearAsync(CancellationToken cancellationToken)
    {
        _current = Unlimited;
        CurrentRawToken = null;
        return Task.CompletedTask;
    }

    private static LicenseStatus Status(LicenseLimits limits) => new(
        LicenseState.Active,
        CustomerName: "Integration Tests",
        Edition: "Test",
        IssuedUtc: DateTime.UtcNow.AddDays(-1),
        ExpiresUtc: DateTime.UtcNow.AddYears(1),
        Limits: limits,
        Features: [],
        InvalidReason: null);

    private static int Int(JsonWebToken token, string claim) =>
        token.TryGetPayloadValue<int>(claim, out var value) ? value : LicenseLimits.Unlimited;

    private static IReadOnlyList<string>? Strings(JsonWebToken token, string claim)
    {
        if (!token.TryGetPayloadValue<JsonElement>(claim, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return value.EnumerateArray().Select(v => v.GetString()).OfType<string>().ToList();
    }
}
