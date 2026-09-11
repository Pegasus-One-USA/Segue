using FHIRBridge.Application.Abstractions.Licensing;
using FHIRBridge.Infrastructure.Licensing;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Licensing;

/// <summary>
/// ⚠ Covers the TEMPORARY, DEV-ONLY <see cref="DevLicenseMintingService"/> — the signing counterpart to
/// <see cref="SignedLicenseValidator"/>, backing the portal's temporary "Dev: Mint a test license" page.
/// The whole point of this suite is proving the dev keypair really round-trips: a token minted here must
/// verify successfully against the REAL, unmodified <see cref="SignedLicenseValidator"/> /
/// <see cref="LicensePublicKey"/> used everywhere else in the product — not a re-implementation of the
/// signing/verification logic.
///
/// Delete this test file alongside DevLicenseMintingService/IDevLicenseMintingService/DevLicenseSigningKey/
/// DevLicenseMintingController once license minting moves to its own separate internal tool.
/// </summary>
public sealed class DevLicenseMintingServiceTests
{
    private readonly IDevLicenseMintingService _sut = new DevLicenseMintingService();

    [Fact]
    public void Minted_token_verifies_as_Active_against_the_real_SignedLicenseValidator()
    {
        var request = new DevLicenseMintRequest(
            CustomerId: "cust-dev-test",
            CustomerName: "Dev Test Hospital",
            Edition: "enterprise",
            ExpiresUtc: DateTime.UtcNow.AddYears(1),
            MaxUsers: 25,
            MaxWorkflows: 10,
            MaxSourceConnections: 4,
            Features: new[] { "hl7-mllp", "deid" });

        var token = _sut.Mint(request);
        var status = SignedLicenseValidator.Validate(token);

        status.State.Should().Be(LicenseState.Active);
        status.CustomerName.Should().Be("Dev Test Hospital");
        status.Edition.Should().Be("enterprise");
        status.Limits.Should().NotBeNull();
        status.Limits!.MaxUsers.Should().Be(25);
        status.Limits.MaxWorkflows.Should().Be(10);
        status.Limits.MaxSourceConnections.Should().Be(4);
        status.Features.Should().BeEquivalentTo(new[] { "hl7-mllp", "deid" });
        status.InvalidReason.Should().BeNull();
    }

    [Fact]
    public void Minted_token_carries_allowedSourceTypes_allowedHospitals_and_maxProcessedRecordsPerMonth()
    {
        var request = new DevLicenseMintRequest(
            CustomerId: "cust-dev-test-2",
            CustomerName: "Dev Test Hospital 2",
            Edition: "enterprise",
            ExpiresUtc: DateTime.UtcNow.AddYears(1),
            MaxUsers: 25,
            MaxWorkflows: 10,
            MaxSourceConnections: 4,
            Features: new[] { "hl7-mllp", "deid" },
            AllowedSourceTypes: new[] { "Epic", "Healow" },
            AllowedHospitals: new[]
            {
                new DevLicenseMintHospital("Epic", "https://epic.mercy.example/fhir/r4", "Mercy Main"),
                new DevLicenseMintHospital("Healow", "https://ecw.mercy.example/fhir", null),
            },
            MaxProcessedRecordsPerMonth: 50000);

        var token = _sut.Mint(request);
        var status = SignedLicenseValidator.Validate(token);

        status.State.Should().Be(LicenseState.Active);
        status.Limits.Should().NotBeNull();
        status.Limits!.AllowedSourceTypes.Should().BeEquivalentTo(new[] { "Epic", "Healow" });
        status.Limits.AllowedHospitals.Should().HaveCount(2);
        status.Limits.AllowedHospitals![0].Vendor.Should().Be("Epic");
        status.Limits.AllowedHospitals[0].BaseUrl.Should().Be("https://epic.mercy.example/fhir/r4");
        status.Limits.AllowedHospitals[0].DisplayName.Should().Be("Mercy Main");
        status.Limits.AllowedHospitals[1].DisplayName.Should().BeNull();
        status.Limits.MaxProcessedRecordsPerMonth.Should().Be(50000);
    }

    [Fact]
    public void Minted_token_carries_allowedResourceTypes_and_allowedDestinationTypes()
    {
        var request = new DevLicenseMintRequest(
            CustomerId: "cust-dev-test-4",
            CustomerName: "Dev Test Hospital 4",
            Edition: "enterprise",
            ExpiresUtc: DateTime.UtcNow.AddYears(1),
            MaxUsers: 25,
            MaxWorkflows: 10,
            MaxSourceConnections: 4,
            Features: new[] { "hl7-mllp", "deid" },
            AllowedResourceTypes: new[] { "Patient", "Observation" },
            AllowedDestinationTypes: new[] { "SqlServer", "Sftp" });

        var token = _sut.Mint(request);
        var status = SignedLicenseValidator.Validate(token);

        status.State.Should().Be(LicenseState.Active);
        status.Limits.Should().NotBeNull();
        status.Limits!.AllowedResourceTypes.Should().BeEquivalentTo(new[] { "Patient", "Observation" });
        status.Limits.AllowedDestinationTypes.Should().BeEquivalentTo(new[] { "SqlServer", "Sftp" });
    }

    [Fact]
    public void Omitted_allowedSourceTypes_allowedHospitals_and_maxProcessedRecordsPerMonth_map_to_null()
    {
        var request = new DevLicenseMintRequest(
            CustomerId: "cust-dev-test-3",
            CustomerName: "Dev Test Hospital 3",
            Edition: "enterprise",
            ExpiresUtc: DateTime.UtcNow.AddYears(1),
            MaxUsers: LicenseLimits.Unlimited,
            MaxWorkflows: LicenseLimits.Unlimited,
            MaxSourceConnections: LicenseLimits.Unlimited,
            Features: null);

        var token = _sut.Mint(request);
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
    public void Omitted_customerName_defaults_to_customerId_same_as_the_CLI_tool()
    {
        var request = new DevLicenseMintRequest(
            CustomerId: "cust-no-name",
            CustomerName: null,
            Edition: null,
            ExpiresUtc: DateTime.UtcNow.AddDays(30),
            MaxUsers: LicenseLimits.Unlimited,
            MaxWorkflows: LicenseLimits.Unlimited,
            MaxSourceConnections: LicenseLimits.Unlimited,
            Features: null);

        var token = _sut.Mint(request);
        var status = SignedLicenseValidator.Validate(token);

        status.State.Should().Be(LicenseState.Active);
        status.CustomerName.Should().Be("cust-no-name");
        status.Edition.Should().Be("standard");
        status.Limits.Should().NotBeNull();
        status.Limits!.MaxUsers.Should().Be(LicenseLimits.Unlimited);
        status.Limits.MaxWorkflows.Should().Be(LicenseLimits.Unlimited);
        status.Limits.MaxSourceConnections.Should().Be(LicenseLimits.Unlimited);
        status.Features.Should().BeEmpty();
    }

    [Fact]
    public void Expired_expiry_mints_a_token_that_the_validator_reports_as_Expired()
    {
        var request = new DevLicenseMintRequest(
            CustomerId: "cust-expired",
            CustomerName: "Expired Co",
            Edition: "standard",
            ExpiresUtc: DateTime.UtcNow.AddDays(-30),
            MaxUsers: LicenseLimits.Unlimited,
            MaxWorkflows: LicenseLimits.Unlimited,
            MaxSourceConnections: LicenseLimits.Unlimited,
            Features: null);

        var token = _sut.Mint(request);
        var status = SignedLicenseValidator.Validate(token);

        status.State.Should().Be(LicenseState.Expired);
        status.CustomerName.Should().Be("Expired Co");
    }
}
