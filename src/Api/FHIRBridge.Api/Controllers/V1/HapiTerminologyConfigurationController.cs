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

    public HapiTerminologyConfigurationController(IHapiTerminologyConfigurationService service, TerminologyImportChannel importChannel)
    {
        _service = service;
        _importChannel = importChannel;
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
}
