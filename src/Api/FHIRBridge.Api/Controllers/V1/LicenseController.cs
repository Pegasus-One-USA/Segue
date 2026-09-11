using FHIRBridge.Application.Abstractions.Licensing;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Mappings;
using FHIRBridge.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

/// <summary>
/// Reports the product's current signed license and lets an admin apply a new one. Verification/reporting
/// only — nothing here (or anywhere else in this stage) blocks or gates product behavior on the license.
/// </summary>
[ApiController]
[Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
[Route("api/v1/license")]
public sealed class LicenseController : ControllerBase
{
    private readonly ILicenseService _licenseService;
    private readonly ILicenseUsageCountsProvider _licenseUsageCountsProvider;
    private readonly ILicenseUsageExecutionStatsProvider _licenseUsageExecutionStatsProvider;

    public LicenseController(
        ILicenseService licenseService,
        ILicenseUsageCountsProvider licenseUsageCountsProvider,
        ILicenseUsageExecutionStatsProvider licenseUsageExecutionStatsProvider)
    {
        _licenseService = licenseService;
        _licenseUsageCountsProvider = licenseUsageCountsProvider;
        _licenseUsageExecutionStatsProvider = licenseUsageExecutionStatsProvider;
    }

    [HttpGet]
    [ProducesResponseType(typeof(LicenseStatusDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var usageCounts = await _licenseUsageCountsProvider.GetCurrentCountsAsync(cancellationToken);
        var executionStats = await _licenseUsageExecutionStatsProvider.GetCurrentStatsAsync(cancellationToken);
        return Ok(LicenseStatusMapper.ToDto(_licenseService.Current, usageCounts, executionStats));
    }

    [HttpPost]
    [ProducesResponseType(typeof(LicenseStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Apply([FromBody] ApplyLicenseRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Token))
        {
            return BadRequest(new { error = "invalid_request", error_description = "token is required." });
        }

        var result = await _licenseService.ApplyAsync(request.Token, cancellationToken);
        if (!result.Succeeded || result.Status is null)
        {
            return BadRequest(new
            {
                error = "invalid_license",
                error_description = result.ErrorMessage ?? "The license token failed verification.",
            });
        }

        // Fresh counts so the portal gets an up-to-date usage snapshot immediately after activating,
        // without a second round-trip to GET.
        var usageCounts = await _licenseUsageCountsProvider.GetCurrentCountsAsync(cancellationToken);
        var executionStats = await _licenseUsageExecutionStatsProvider.GetCurrentStatsAsync(cancellationToken);
        return Ok(LicenseStatusMapper.ToDto(result.Status, usageCounts, executionStats));
    }
}
