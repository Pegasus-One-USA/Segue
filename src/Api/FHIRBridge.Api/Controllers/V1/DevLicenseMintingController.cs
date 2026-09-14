using System.Linq;
using FHIRBridge.Application.Security;
using FHIRBridge.Infrastructure.Licensing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;

namespace FHIRBridge.Api.Controllers.V1;

// ⚠️⚠️⚠️ TEMPORARY / DEV-ONLY — NEVER FOR PRODUCTION, NEVER FOR A REAL CUSTOMER ⚠️⚠️⚠️
//
// This controller SIGNS license tokens with the throwaway private key in DevLicenseSigningKey — a
// capability the shipped product deliberately never exposes anywhere else (LicenseController /
// SignedLicenseValidator / LicenseService are verify-only; see LicensePublicKey's remarks on why signing
// stays out of the API/Infrastructure entirely). It exists ONLY to back the portal's temporary
// "Dev: Mint a test license" page (portal/src/app/settings/pages/license-dev-mint), so a developer can
// generate and apply a test license without running tools/FHIRBridge.LicenseMinter by hand.
//
// THE Mint() METHOD'S IHostEnvironment.IsDevelopment() CHECK BELOW IS THE ACTUAL SECURITY BOUNDARY — it
// 404s the endpoint on any host that isn't running in the Development environment, checked as the very
// first thing in the action body, before any other logic runs. This is NOT optional and must never be
// removed, weakened, or bypassed. [Authorize(UnifiedAdmin)] alone is not sufficient: it protects against an
// unauthenticated caller, but the Development gate is what prevents this from ever being reachable in a
// deployed/production/staging host in the first place.
//
// DELETE THIS WHOLE CONTROLLER — along with DevLicenseSigningKey, IDevLicenseMintingService, and
// DevLicenseMintingService — once license minting moves to its own separate internal tool/portal, which was
// always the stated plan for this temporary stand-in.
/// <summary>Temporary dev-only license-minting endpoint. See the file-level comment above.</summary>
[ApiController]
[Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
[Route("api/v1/dev/license-mint")]
public sealed class DevLicenseMintingController : ControllerBase
{
    private readonly IDevLicenseMintingService _mintingService;
    private readonly IHostEnvironment _environment;

    public DevLicenseMintingController(IDevLicenseMintingService mintingService, IHostEnvironment environment)
    {
        _mintingService = mintingService;
        _environment = environment;
    }

    [HttpPost]
    [ProducesResponseType(typeof(DevLicenseMintResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Mint([FromBody] DevLicenseMintRequestDto request)
    {
        // Hard gate — see the file-level comment above. Checked before anything else in this action, on
        // every request, regardless of caller/auth: this endpoint must not exist outside Development.
        if (!_environment.IsDevelopment())
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(request.CustomerId))
        {
            return BadRequest(new { error = "invalid_request", error_description = "customerId is required." });
        }

        var token = _mintingService.Mint(new DevLicenseMintRequest(
            request.CustomerId,
            request.CustomerName,
            request.Edition,
            request.ExpiresUtc,
            request.MaxUsers,
            request.MaxWorkflows,
            request.MaxSourceConnections,
            request.Features,
            request.AllowedSourceTypes,
            request.AllowedHospitals?.Select(h => new DevLicenseMintHospital(h.Vendor, h.BaseUrl, h.DisplayName)).ToArray(),
            request.MaxProcessedRecordsPerMonth,
            request.AllowedResourceTypes,
            request.AllowedDestinationTypes,
            request.MaxSuccessfulWorkflowExecutionsPerMonth));

        return Ok(new DevLicenseMintResponse(token));
    }
}

/// <summary>Body of one entry in <see cref="DevLicenseMintRequestDto.AllowedHospitals"/> — mirrors
/// <see cref="DevLicenseMintHospital"/> field for field.</summary>
public sealed record DevLicenseMintHospitalDto(string Vendor, string BaseUrl, string? DisplayName);

/// <summary>Body of the temporary <c>POST /api/v1/dev/license-mint</c> endpoint — mirrors
/// <see cref="DevLicenseMintRequest"/> field for field.</summary>
public sealed record DevLicenseMintRequestDto(
    string CustomerId,
    string? CustomerName,
    string? Edition,
    DateTime ExpiresUtc,
    int MaxUsers,
    int MaxWorkflows,
    int MaxSourceConnections,
    IReadOnlyList<string>? Features,
    IReadOnlyList<string>? AllowedSourceTypes = null,
    IReadOnlyList<DevLicenseMintHospitalDto>? AllowedHospitals = null,
    int MaxProcessedRecordsPerMonth = FHIRBridge.Application.Abstractions.Licensing.LicenseLimits.Unlimited,
    IReadOnlyList<string>? AllowedResourceTypes = null,
    IReadOnlyList<string>? AllowedDestinationTypes = null,
    int MaxSuccessfulWorkflowExecutionsPerMonth = FHIRBridge.Application.Abstractions.Licensing.LicenseLimits.Unlimited);

/// <summary>Response of the temporary <c>POST /api/v1/dev/license-mint</c> endpoint.</summary>
public sealed record DevLicenseMintResponse(string Token);
