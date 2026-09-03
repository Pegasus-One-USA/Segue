using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using FHIRBridge.Application.Services.Terminology;
using FHIRBridge.Infrastructure.Terminology;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace FHIRBridge.Api.Controllers.V1;

/// <summary>
/// Grouped settings, manual "Run Now" trigger, and run history for the 13 HAPI-terminology-server sync
/// systems (Settings → System Settings → General → Terminology Settings). Gated the same way as the rest
/// of that screen (SuperAdminOnly), not the granular per-permission model the older Loinc/Snomed/RxNorm
/// configuration controllers use — this stays consistent with SystemSettingsController, which already
/// owns every other row on this page.
/// </summary>
[ApiController]
[Authorize(Policy = AuthorizationPolicies.SuperAdminOnly)]
[Route("api/v1/terminology/hapi")]
public sealed class HapiTerminologyConfigurationController : ControllerBase
{
    private readonly IHapiTerminologyConfigurationService _service;
    private readonly TerminologyImportChannel _importChannel;
    private readonly ITerminologyConceptService _conceptService;

    public HapiTerminologyConfigurationController(
        IHapiTerminologyConfigurationService service, TerminologyImportChannel importChannel, ITerminologyConceptService conceptService)
    {
        _service = service;
        _importChannel = importChannel;
        _conceptService = conceptService;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<HapiTerminologyConfigurationDto>>> GetAll(CancellationToken cancellationToken) =>
        Ok(await _service.GetAllAsync(cancellationToken));

    [HttpGet("{code}")]
    public async Task<ActionResult<HapiTerminologyConfigurationDto>> Get(string code, CancellationToken cancellationToken) =>
        Ok(await _service.GetAsync(code, cancellationToken));

    [HttpPut("{code}")]
    public async Task<ActionResult<HapiTerminologyConfigurationDto>> Update(
        string code, [FromBody] UpdateHapiTerminologyConfigurationRequest request, CancellationToken cancellationToken) =>
        Ok(await _service.UpdateAsync(code, request, cancellationToken));

    [HttpPost("{code}/run-now")]
    public IActionResult RunNow(string code)
    {
        _importChannel.Enqueue(async (services, ct) =>
        {
            var service = services.GetRequiredService<IHapiTerminologyConfigurationService>();
            await service.RunAndRecordHistoryAsync(code, ct);
        });

        return Accepted(new { message = "Synchronization started in the background. Check history for progress." });
    }

    [HttpGet("{code}/history")]
    public async Task<ActionResult<IReadOnlyList<HapiTerminologyImportHistoryEntryDto>>> History(string code, CancellationToken cancellationToken) =>
        Ok(await _service.GetHistoryAsync(code, cancellationToken));

    /// <summary>Server-side paged, searchable browse of one system's locally stored codes — backs
    /// the "View All Codes" screen under this system's ⋮ menu.</summary>
    [HttpGet("{code}/codes")]
    public async Task<ActionResult<PagedResult<TerminologyConceptDto>>> GetCodes(
        string code, [FromQuery] string? search, [FromQuery] int page, [FromQuery] int pageSize, CancellationToken cancellationToken) =>
        Ok(await _conceptService.GetPagedAsync(code, search, page <= 0 ? 1 : page, pageSize <= 0 ? 25 : pageSize, cancellationToken));

    [HttpPost("{code}/codes")]
    public async Task<ActionResult<TerminologyConceptDto>> AddCode(
        string code, [FromBody] UpsertTerminologyConceptRequest request, CancellationToken cancellationToken) =>
        Ok(await _conceptService.AddAsync(code, request, cancellationToken));

    [HttpPut("{code}/codes/{pid:long}")]
    public async Task<ActionResult<TerminologyConceptDto>> UpdateCode(
        string code, long pid, [FromBody] UpsertTerminologyConceptRequest request, CancellationToken cancellationToken) =>
        Ok(await _conceptService.UpdateAsync(code, pid, request, cancellationToken));

    [HttpDelete("{code}/codes/{pid:long}")]
    public async Task<IActionResult> DeleteCode(string code, long pid, CancellationToken cancellationToken)
    {
        await _conceptService.DeleteAsync(code, pid, cancellationToken);
        return NoContent();
    }
}
