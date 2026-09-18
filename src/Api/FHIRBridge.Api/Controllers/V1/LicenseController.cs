using FHIRBridge.Application.Abstractions.Licensing;
using FHIRBridge.Application.Abstractions.Persistence;
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
    private readonly ILicenseHistoryRepository _licenseHistoryRepository;

    public LicenseController(
        ILicenseService licenseService,
        ILicenseUsageCountsProvider licenseUsageCountsProvider,
        ILicenseUsageExecutionStatsProvider licenseUsageExecutionStatsProvider,
        ILicenseHistoryRepository licenseHistoryRepository)
    {
        _licenseService = licenseService;
        _licenseUsageCountsProvider = licenseUsageCountsProvider;
        _licenseUsageExecutionStatsProvider = licenseUsageExecutionStatsProvider;
        _licenseHistoryRepository = licenseHistoryRepository;
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

    /// <summary>Every license this install has ever successfully applied, newest first — the current one
    /// (same token <see cref="ILicenseService.CurrentRawToken"/> holds) is flagged
    /// <see cref="LicenseHistoryEntryDto.IsCurrent"/>.</summary>
    [HttpGet("history")]
    [ProducesResponseType(typeof(IReadOnlyList<LicenseHistoryEntryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetHistory(CancellationToken cancellationToken)
    {
        var entries = await _licenseHistoryRepository.GetAllAsync(cancellationToken);
        var currentToken = _licenseService.CurrentRawToken;

        return Ok(entries.Select(e => new LicenseHistoryEntryDto(
            e.AppliedUtc, e.CustomerName, e.Edition, e.State, e.ExpiresUtc,
            currentToken is not null && e.Token == currentToken)));
    }
}
