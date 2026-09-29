using FHIRBridge.Application.Abstractions.Licensing;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Mappings;
using FHIRBridge.Application.Security;
using FHIRBridge.Infrastructure.Licensing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

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
    /// <summary>Off by default — same escape-hatch pattern as
    /// <c>DataProtection:AllowAppSecretRegeneration</c>. <see cref="Clear"/>/<see cref="ClearHistory"/> are
    /// destructive install-wide actions reachable by any <c>UnifiedAdmin</c> (GlobalAdmin OR TenantAdmin —
    /// see <c>AuthorizationPolicies</c>), so a customer's own tenant admin could otherwise revert a
    /// production install to Unlicensed with one click. Enabled only in Development's appsettings.</summary>
    private const string AllowTestingUtilitiesKey = "License:AllowTestingUtilities";

    private readonly ILicenseService _licenseService;
    private readonly ILicenseUsageCountsProvider _licenseUsageCountsProvider;
    private readonly ILicenseUsageExecutionStatsProvider _licenseUsageExecutionStatsProvider;
    private readonly ILicenseHistoryRepository _licenseHistoryRepository;
    private readonly IConfiguration _configuration;

    public LicenseController(
        ILicenseService licenseService,
        ILicenseUsageCountsProvider licenseUsageCountsProvider,
        ILicenseUsageExecutionStatsProvider licenseUsageExecutionStatsProvider,
        ILicenseHistoryRepository licenseHistoryRepository,
        IConfiguration configuration)
    {
        _licenseService = licenseService;
        _licenseUsageCountsProvider = licenseUsageCountsProvider;
        _licenseUsageExecutionStatsProvider = licenseUsageExecutionStatsProvider;
        _licenseHistoryRepository = licenseHistoryRepository;
        _configuration = configuration;
    }

    private bool AllowTestingUtilities => _configuration.GetValue(AllowTestingUtilitiesKey, false);

    [HttpGet]
    [ProducesResponseType(typeof(LicenseStatusDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var usageCounts = await _licenseUsageCountsProvider.GetCurrentCountsAsync(cancellationToken);
        var executionStats = await _licenseUsageExecutionStatsProvider.GetCurrentStatsAsync(cancellationToken);
        return Ok(LicenseStatusMapper.ToDto(
            _licenseService.Current, usageCounts, executionStats, allowTestingUtilities: AllowTestingUtilities));
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
        return Ok(LicenseStatusMapper.ToDto(
            result.Status, usageCounts, executionStats, result.AlreadyActive, AllowTestingUtilities));
    }

    /// <summary>Every license this install has ever successfully applied, newest first — the current one
    /// (same token <see cref="ILicenseService.CurrentRawToken"/> holds) is flagged
    /// <see cref="LicenseHistoryEntryDto.IsCurrent"/>. Each row's own stored token is re-parsed (never
    /// re-verified against anything live — it already applied successfully once) so the portal can show
    /// every quota/restriction dimension for any past license, not just the current one.</summary>
    [HttpGet("history")]
    [ProducesResponseType(typeof(IReadOnlyList<LicenseHistoryEntryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetHistory(CancellationToken cancellationToken)
    {
        var entries = await _licenseHistoryRepository.GetAllAsync(cancellationToken);
        var currentToken = _licenseService.CurrentRawToken;

        return Ok(entries.Select(e =>
        {
            var parsed = SignedLicenseValidator.Validate(e.Token);
            return new LicenseHistoryEntryDto(
                e.Id, e.AppliedUtc, e.CustomerName, e.Edition, e.State, e.ExpiresUtc,
                currentToken is not null && e.Token == currentToken,
                parsed.IssuedUtc, LicenseStatusMapper.ToLimitsDto(parsed.Limits), parsed.Features, parsed.RequestKey);
        }));
    }

    /// <summary>TESTING/SUPPORT UTILITY ONLY — removes the currently-applied license entirely, reverting
    /// this install to Unlicensed. Never called by the normal apply flow. Leaves
    /// <see cref="ILicenseHistoryRepository"/>'s rows untouched — see <see cref="ClearHistory"/> for that.
    /// 404s unless <see cref="AllowTestingUtilitiesKey"/> is explicitly enabled — see its own remarks.</summary>
    [HttpDelete]
    [ProducesResponseType(typeof(LicenseStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Clear(CancellationToken cancellationToken)
    {
        if (!AllowTestingUtilities)
        {
            return NotFound();
        }

        await _licenseService.ClearAsync(cancellationToken);

        var usageCounts = await _licenseUsageCountsProvider.GetCurrentCountsAsync(cancellationToken);
        var executionStats = await _licenseUsageExecutionStatsProvider.GetCurrentStatsAsync(cancellationToken);
        return Ok(LicenseStatusMapper.ToDto(
            _licenseService.Current, usageCounts, executionStats, allowTestingUtilities: true));
    }

    /// <summary>TESTING/SUPPORT UTILITY ONLY — soft-deletes every <see cref="LicenseHistoryEntryDto"/> row
    /// (hidden from the History tab, but recoverable directly from the database, never hard-deleted). Does
    /// not touch the currently-applied license itself — see <see cref="Clear"/> for that. 404s unless
    /// <see cref="AllowTestingUtilitiesKey"/> is explicitly enabled — see its own remarks.</summary>
    [HttpDelete("history")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ClearHistory(CancellationToken cancellationToken)
    {
        if (!AllowTestingUtilities)
        {
            return NotFound();
        }

        await _licenseHistoryRepository.ClearAllAsync(cancellationToken);
        return NoContent();
    }
}
