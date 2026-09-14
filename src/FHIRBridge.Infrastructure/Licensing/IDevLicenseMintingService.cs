using FHIRBridge.Application.Abstractions.Licensing;

namespace FHIRBridge.Infrastructure.Licensing;

/// <summary>
/// ⚠ TEMPORARY / DEV-ONLY — signs throwaway test license tokens for the portal's temporary "Dev: Mint a
/// test license" page. See <see cref="DevLicenseSigningKey"/>'s remarks for the full security rationale and
/// <see cref="DevLicenseMintingService"/> for the implementation. Never called except from
/// <c>FHIRBridge.Api.Controllers.V1.DevLicenseMintingController</c>, which is itself hard-gated to
/// <c>IHostEnvironment.IsDevelopment()</c>.
///
/// DELETE THIS FILE alongside <see cref="DevLicenseSigningKey"/>, <see cref="DevLicenseMintingService"/>,
/// and <c>DevLicenseMintingController</c> once license minting moves to its own separate internal tool.
/// </summary>
public interface IDevLicenseMintingService
{
    /// <summary>Mints and signs a compact-JWS license token from <paramref name="request"/>, using the
    /// same claim names <c>SignedLicenseValidator</c> and <c>tools/FHIRBridge.LicenseMinter</c> already
    /// agree on. Never throws for a well-formed request; the caller (the controller) is responsible for
    /// basic input validation (e.g. a non-empty customer id) before calling this.</summary>
    string Mint(DevLicenseMintRequest request);
}

/// <summary>Mirrors <c>FHIRBridge.Application.Abstractions.Licensing.AllowedHospital</c> field for field —
/// duplicated here (rather than referenced) so this Infrastructure-layer minting stand-in doesn't need to
/// reach into Application.DTOs just for this one shape. See <see cref="DevLicenseMintRequest"/>.</summary>
public sealed record DevLicenseMintHospital(string Vendor, string BaseUrl, string? DisplayName);

/// <summary>
/// Fields needed to mint a dev/test-only signed license token — mirrors, field for field, the arguments
/// <c>tools/FHIRBridge.LicenseMinter</c> already accepts (customer id/name, edition, expiry, the three
/// independently-set quota limits, a features list, and the three source-restriction dimensions). Every
/// numeric limit uses <see cref="LicenseLimits.Unlimited"/> (<c>-1</c>) to mean unlimited for that
/// dimension — same convention as the CLI tool and <c>SignedLicenseValidator</c>. An empty/null
/// <see cref="Features"/>/<see cref="AllowedSourceTypes"/>/<see cref="AllowedHospitals"/> means no extra
/// features / unrestricted for that dimension.
/// </summary>
public sealed record DevLicenseMintRequest(
    string CustomerId,
    string? CustomerName,
    string? Edition,
    DateTime ExpiresUtc,
    int MaxUsers,
    int MaxWorkflows,
    int MaxSourceConnections,
    IReadOnlyList<string>? Features,
    IReadOnlyList<string>? AllowedSourceTypes = null,
    IReadOnlyList<DevLicenseMintHospital>? AllowedHospitals = null,
    int MaxProcessedRecordsPerMonth = LicenseLimits.Unlimited,
    /// <summary>FHIR resource type names (<c>SupportedFhirResourceTypes.All</c> members) this license
    /// permits processing. <c>null</c>/empty means every resource type is allowed.</summary>
    IReadOnlyList<string>? AllowedResourceTypes = null,
    /// <summary>Destination type names (<c>DestinationType</c> members) this license permits writing to.
    /// <c>null</c>/empty means every destination type is allowed.</summary>
    IReadOnlyList<string>? AllowedDestinationTypes = null,
    /// <summary>Hard cap on successful pipeline/workflow executions (both planes combined) per calendar
    /// month. <see cref="LicenseLimits.Unlimited"/> means unlimited.</summary>
    int MaxSuccessfulWorkflowExecutionsPerMonth = LicenseLimits.Unlimited);
