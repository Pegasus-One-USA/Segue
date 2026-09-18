using System.Security.Cryptography;
using System.Text.Json;
using FHIRBridge.Application.Abstractions.Licensing;
using FHIRBridge.Infrastructure.Licensing;
using FluentAssertions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace FHIRBridge.UnitTests.Licensing;

/// <summary>
/// Covers <see cref="SignedLicenseValidator"/> end to end against the real dev keypair embedded in
/// <see cref="LicensePublicKey"/> / <c>tools/FHIRBridge.LicenseMinter</c>.
/// </summary>
public sealed class SignedLicenseValidatorTests
{
    // Same dev keypair as src/FHIRBridge.Infrastructure/Licensing/LicensePublicKey.cs and
    // tools/FHIRBridge.LicenseMinter's --dev-key fallback — duplicated here deliberately (per the spec,
    // this test suite must not reference the standalone, deliberately-not-in-the-solution minting tool
    // project) so this test can mint tokens the validator's embedded dev public key will actually verify.
    private const string DevPrivateKeyPkcs8Base64 =
        "MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQgzNfRUjAwdWWhtF6HDTFMjzkpc6ix4tAt0KpVuBS1H+mhRANCAAQnMOZWXkeX06SoZyVY2NFCQjAPD9dXCmyoZChc3n59+CHbwMLDg8lzcuh/60NbOQzfHLB/fGxuUpTM+n5l2sJr";

    [Fact]
    public void Valid_token_round_trips_to_Active_with_parsed_claims()
    {
        // Deliberately not disposed here: Microsoft.IdentityModel.Tokens caches signature providers keyed
        // off the key material, and disposing this ECDsa immediately after minting has caused a LATER test
        // that mints with the same dev key to hit an ObjectDisposedException from that cached provider.
        // Letting the GC reclaim it avoids that cross-test collision.
        var key = LoadDevPrivateKey();
        var token = MintToken(key, DateTime.UtcNow.AddMinutes(-10), DateTime.UtcNow.AddYears(1));

        var status = SignedLicenseValidator.Validate(token);

        status.State.Should().Be(LicenseState.Active);
        status.CustomerName.Should().Be("Mercy Health System");
        status.Edition.Should().Be("enterprise");
        status.Limits.Should().NotBeNull();
        status.Limits!.MaxUsers.Should().Be(50);
        status.Limits.MaxWorkflows.Should().Be(20);
        status.Limits.MaxSourceConnections.Should().Be(10);
        status.Features.Should().BeEquivalentTo(new[] { "hl7-mllp", "deid", "runtime-plane" });
        status.InvalidReason.Should().BeNull();
    }

    [Fact]
    public void Expired_token_reports_Expired_state_with_claims_still_populated()
    {
        // Deliberately not disposed here: Microsoft.IdentityModel.Tokens caches signature providers keyed
        // off the key material, and disposing this ECDsa immediately after minting has caused a LATER test
        // that mints with the same dev key to hit an ObjectDisposedException from that cached provider.
        // Letting the GC reclaim it avoids that cross-test collision.
        var key = LoadDevPrivateKey();
        var token = MintToken(key, DateTime.UtcNow.AddYears(-1), DateTime.UtcNow.AddDays(-30));

        var status = SignedLicenseValidator.Validate(token);

        status.State.Should().Be(LicenseState.Expired);
        status.CustomerName.Should().Be("Mercy Health System");
        status.Edition.Should().Be("enterprise");
        status.Limits.Should().NotBeNull();
        status.Limits!.MaxUsers.Should().Be(50);
    }

    [Fact]
    public void NotYetValid_token_reports_Invalid()
    {
        // Documented choice (see SignedLicenseValidator's remarks): a token whose nbf is still in the
        // future maps to Invalid, not Grace — Grace is reserved for a possible future post-expiry window.
        // Deliberately not disposed here: Microsoft.IdentityModel.Tokens caches signature providers keyed
        // off the key material, and disposing this ECDsa immediately after minting has caused a LATER test
        // that mints with the same dev key to hit an ObjectDisposedException from that cached provider.
        // Letting the GC reclaim it avoids that cross-test collision.
        var key = LoadDevPrivateKey();
        var token = MintToken(key, DateTime.UtcNow.AddDays(30), DateTime.UtcNow.AddYears(1));

        var status = SignedLicenseValidator.Validate(token);

        status.State.Should().Be(LicenseState.Invalid);
        status.InvalidReason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Tampered_payload_reports_Invalid()
    {
        // Deliberately not disposed here: Microsoft.IdentityModel.Tokens caches signature providers keyed
        // off the key material, and disposing this ECDsa immediately after minting has caused a LATER test
        // that mints with the same dev key to hit an ObjectDisposedException from that cached provider.
        // Letting the GC reclaim it avoids that cross-test collision.
        var key = LoadDevPrivateKey();
        var token = MintToken(key, DateTime.UtcNow.AddMinutes(-10), DateTime.UtcNow.AddYears(1));

        var parts = token.Split('.');
        parts.Should().HaveCount(3);
        var tamperedToken = string.Join('.', parts[0], FlipOneChar(parts[1]), parts[2]);

        var status = SignedLicenseValidator.Validate(tamperedToken);

        status.State.Should().Be(LicenseState.Invalid);
        status.InvalidReason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Token_signed_with_a_different_keypair_reports_Invalid()
    {
        var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var token = MintToken(otherKey, DateTime.UtcNow.AddMinutes(-10), DateTime.UtcNow.AddYears(1));

        var status = SignedLicenseValidator.Validate(token);

        status.State.Should().Be(LicenseState.Invalid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("this-is-not-a-jwt-at-all")]
    [InlineData("garbage.garbage.garbage")]
    public void Garbage_input_reports_Invalid_without_throwing(string garbage)
    {
        LicenseStatus? status = null;
        Action act = () => status = SignedLicenseValidator.Validate(garbage);

        act.Should().NotThrow();
        status!.State.Should().Be(LicenseState.Invalid);
    }

    [Fact]
    public void Missing_maxUsers_claim_maps_to_Unlimited()
    {
        // Deliberately not disposed here: Microsoft.IdentityModel.Tokens caches signature providers keyed
        // off the key material, and disposing this ECDsa immediately after minting has caused a LATER test
        // that mints with the same dev key to hit an ObjectDisposedException from that cached provider.
        // Letting the GC reclaim it avoids that cross-test collision.
        var key = LoadDevPrivateKey();
        var token = MintToken(key, DateTime.UtcNow.AddMinutes(-10), DateTime.UtcNow.AddYears(1), maxUsers: null);

        var status = SignedLicenseValidator.Validate(token);

        status.State.Should().Be(LicenseState.Active);
        status.Limits.Should().NotBeNull();
        status.Limits!.MaxUsers.Should().Be(LicenseLimits.Unlimited);
    }

    [Fact]
    public void Present_allowedSourceTypes_allowedHospitals_and_maxProcessedRecordsPerMonth_round_trip()
    {
        // Deliberately not disposed here: Microsoft.IdentityModel.Tokens caches signature providers keyed
        // off the key material, and disposing this ECDsa immediately after minting has caused a LATER test
        // that mints with the same dev key to hit an ObjectDisposedException from that cached provider.
        // Letting the GC reclaim it avoids that cross-test collision.
        var key = LoadDevPrivateKey();
        var token = MintToken(
            key,
            DateTime.UtcNow.AddMinutes(-10),
            DateTime.UtcNow.AddYears(1),
            allowedSourceTypes: new[] { "Epic", "Healow" },
            allowedHospitals: new object[]
            {
                new { vendor = "Epic", baseUrl = "https://epic.mercy.example/fhir/r4", displayName = "Mercy Main" },
                new { vendor = "Healow", baseUrl = "https://ecw.mercy.example/fhir", displayName = (string?)null },
            },
            maxProcessedRecordsPerMonth: 50000);

        var status = SignedLicenseValidator.Validate(token);

        status.State.Should().Be(LicenseState.Active);
        status.Limits.Should().NotBeNull();
        status.Limits!.AllowedSourceTypes.Should().BeEquivalentTo(new[] { "Epic", "Healow" });
        status.Limits.AllowedHospitals.Should().HaveCount(2);
        status.Limits.AllowedHospitals![0].Vendor.Should().Be("Epic");
        status.Limits.AllowedHospitals[0].BaseUrl.Should().Be("https://epic.mercy.example/fhir/r4");
        status.Limits.AllowedHospitals[0].DisplayName.Should().Be("Mercy Main");
        status.Limits.AllowedHospitals[1].Vendor.Should().Be("Healow");
        status.Limits.AllowedHospitals[1].DisplayName.Should().BeNull();
        status.Limits.MaxProcessedRecordsPerMonth.Should().Be(50000);
    }

    [Fact]
    public void Absent_allowedSourceTypes_allowedHospitals_and_maxProcessedRecordsPerMonth_map_to_null()
    {
        // Deliberately not disposed here: Microsoft.IdentityModel.Tokens caches signature providers keyed
        // off the key material, and disposing this ECDsa immediately after minting has caused a LATER test
        // that mints with the same dev key to hit an ObjectDisposedException from that cached provider.
        // Letting the GC reclaim it avoids that cross-test collision.
        var key = LoadDevPrivateKey();
        var token = MintToken(key, DateTime.UtcNow.AddMinutes(-10), DateTime.UtcNow.AddYears(1));

        var status = SignedLicenseValidator.Validate(token);

        status.State.Should().Be(LicenseState.Active);
        status.Limits.Should().NotBeNull();
        status.Limits!.AllowedSourceTypes.Should().BeNull();
        status.Limits.AllowedHospitals.Should().BeNull();
        status.Limits.MaxProcessedRecordsPerMonth.Should().Be(LicenseLimits.Unlimited);
        status.Limits.AllowedResourceTypes.Should().BeNull();
        status.Limits.AllowedDestinationTypes.Should().BeNull();
    }

    [Fact]
    public void Present_allowedResourceTypes_and_allowedDestinationTypes_round_trip()
    {
        // Deliberately not disposed here: Microsoft.IdentityModel.Tokens caches signature providers keyed
        // off the key material, and disposing this ECDsa immediately after minting has caused a LATER test
        // that mints with the same dev key to hit an ObjectDisposedException from that cached provider.
        // Letting the GC reclaim it avoids that cross-test collision.
        var key = LoadDevPrivateKey();
        var token = MintToken(
            key,
            DateTime.UtcNow.AddMinutes(-10),
            DateTime.UtcNow.AddYears(1),
            allowedResourceTypes: new[] { "Patient", "Observation" },
            allowedDestinationTypes: new[] { "SqlServer", "Sftp" });

        var status = SignedLicenseValidator.Validate(token);

        status.State.Should().Be(LicenseState.Active);
        status.Limits.Should().NotBeNull();
        status.Limits!.AllowedResourceTypes.Should().BeEquivalentTo(new[] { "Patient", "Observation" });
        status.Limits.AllowedDestinationTypes.Should().BeEquivalentTo(new[] { "SqlServer", "Sftp" });
    }

    [Fact]
    public void Absent_allowedResourceTypes_and_allowedDestinationTypes_map_to_null()
    {
        // Deliberately not disposed here: Microsoft.IdentityModel.Tokens caches signature providers keyed
        // off the key material, and disposing this ECDsa immediately after minting has caused a LATER test
        // that mints with the same dev key to hit an ObjectDisposedException from that cached provider.
        // Letting the GC reclaim it avoids that cross-test collision.
        var key = LoadDevPrivateKey();
        var token = MintToken(key, DateTime.UtcNow.AddMinutes(-10), DateTime.UtcNow.AddYears(1));

        var status = SignedLicenseValidator.Validate(token);

        status.State.Should().Be(LicenseState.Active);
        status.Limits.Should().NotBeNull();
        status.Limits!.AllowedResourceTypes.Should().BeNull();
        status.Limits.AllowedDestinationTypes.Should().BeNull();
    }

    [Fact]
    public void Tampered_allowedHospitals_claim_still_fails_signature_verification()
    {
        var key = LoadDevPrivateKey();
        var token = MintToken(
            key,
            DateTime.UtcNow.AddMinutes(-10),
            DateTime.UtcNow.AddYears(1),
            allowedHospitals: new object[]
            {
                new { vendor = "Epic", baseUrl = "https://epic.mercy.example/fhir/r4", displayName = (string?)"Mercy Main" },
            });

        var parts = token.Split('.');
        parts.Should().HaveCount(3);
        var tamperedToken = string.Join('.', parts[0], FlipOneChar(parts[1]), parts[2]);

        var status = SignedLicenseValidator.Validate(tamperedToken);

        status.State.Should().Be(LicenseState.Invalid);
        status.Limits.Should().BeNull();
        status.InvalidReason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Activation_window_is_ignored_by_default_even_when_long_passed()
    {
        // Deliberately not disposed here: see the repeated remark on the tests above.
        var key = LoadDevPrivateKey();
        var token = MintToken(
            key,
            DateTime.UtcNow.AddDays(-30),
            DateTime.UtcNow.AddYears(1),
            activateByUtc: DateTime.UtcNow.AddDays(-29));

        // The single-arg overload (what ReloadAsync uses on every process restart) must never enforce
        // this claim — an already-applied, currently-running license can't be allowed to brick itself
        // just because real time passed its original activation deadline.
        var status = SignedLicenseValidator.Validate(token);

        status.State.Should().Be(LicenseState.Active);
    }

    [Fact]
    public void Activation_window_enforced_and_expired_reports_Invalid()
    {
        var key = LoadDevPrivateKey();
        var token = MintToken(
            key,
            DateTime.UtcNow.AddHours(-2),
            DateTime.UtcNow.AddYears(1),
            activateByUtc: DateTime.UtcNow.AddHours(-1));

        var status = SignedLicenseValidator.Validate(token, enforceActivationWindow: true);

        status.State.Should().Be(LicenseState.Invalid);
        status.InvalidReason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Activation_window_uses_a_short_tolerance_not_the_5_minute_nbf_exp_skew()
    {
        // Regression test for a real observed bug: a 1-minute window minted, applied 3 minutes later,
        // incorrectly reported Active because the activation check originally reused the 5-minute
        // nbf/exp ClockSkew — turning every window into "window + 5 minutes". A deadline 3 minutes in
        // the past must be enforced (Invalid), not tolerated.
        var key = LoadDevPrivateKey();
        var token = MintToken(
            key,
            DateTime.UtcNow.AddMinutes(-4),
            DateTime.UtcNow.AddYears(1),
            activateByUtc: DateTime.UtcNow.AddMinutes(-3));

        var status = SignedLicenseValidator.Validate(token, enforceActivationWindow: true);

        status.State.Should().Be(LicenseState.Invalid);
    }

    [Fact]
    public void Activation_window_enforced_and_still_open_reports_Active()
    {
        // Deliberately not disposed here: see the repeated remark on the tests above.
        var key = LoadDevPrivateKey();
        var token = MintToken(
            key,
            DateTime.UtcNow.AddMinutes(-10),
            DateTime.UtcNow.AddYears(1),
            activateByUtc: DateTime.UtcNow.AddMinutes(50));

        var status = SignedLicenseValidator.Validate(token, enforceActivationWindow: true);

        status.State.Should().Be(LicenseState.Active);
    }

    [Fact]
    public void Activation_window_enforced_but_absent_claim_never_blocks()
    {
        // Deliberately not disposed here: see the repeated remark on the tests above.
        var key = LoadDevPrivateKey();
        var token = MintToken(key, DateTime.UtcNow.AddMinutes(-10), DateTime.UtcNow.AddYears(1));

        var status = SignedLicenseValidator.Validate(token, enforceActivationWindow: true);

        status.State.Should().Be(LicenseState.Active);
    }

    private static ECDsa LoadDevPrivateKey()
    {
        var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(DevPrivateKeyPkcs8Base64), out _);
        return ecdsa;
    }

    private static string MintToken(
        ECDsa signingKey,
        DateTime nbfUtc,
        DateTime expUtc,
        string issuer = "pegasusone",
        string sub = "cust-mercy-health",
        string customerName = "Mercy Health System",
        string edition = "enterprise",
        int? maxUsers = 50,
        int? maxWorkflows = 20,
        int? maxSourceConnections = 10,
        string[]? features = null,
        string[]? allowedSourceTypes = null,
        object[]? allowedHospitals = null,
        int? maxProcessedRecordsPerMonth = null,
        string[]? allowedResourceTypes = null,
        string[]? allowedDestinationTypes = null,
        DateTime? activateByUtc = null)
    {
        features ??= new[] { "hl7-mllp", "deid", "runtime-plane" };

        var payloadJson = JsonSerializer.Serialize(new
        {
            iss = issuer,
            sub,
            customerName,
            edition,
            nbf = ToUnixSeconds(nbfUtc),
            exp = ToUnixSeconds(expUtc),
            maxUsers,
            maxWorkflows,
            maxSourceConnections,
            features,
            allowedSourceTypes,
            allowedHospitals,
            maxProcessedRecordsPerMonth,
            allowedResourceTypes,
            allowedDestinationTypes,
            activateByUtc = activateByUtc.HasValue ? ToUnixSeconds(activateByUtc.Value) : (long?)null,
        });

        var credentials = new SigningCredentials(new ECDsaSecurityKey(signingKey), SecurityAlgorithms.EcdsaSha256);
        return new JsonWebTokenHandler().CreateToken(payloadJson, credentials);
    }

    private static long ToUnixSeconds(DateTime value) =>
        new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)).ToUnixTimeSeconds();

    private static string FlipOneChar(string value)
    {
        var chars = value.ToCharArray();
        var index = chars.Length / 2;
        chars[index] = chars[index] == 'A' ? 'B' : 'A';
        return new string(chars);
    }
}
