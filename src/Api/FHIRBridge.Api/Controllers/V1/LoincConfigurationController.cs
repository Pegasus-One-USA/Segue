using FHIRBridge.Api.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using FHIRBridge.Application.Services;
using FHIRBridge.Application.Abstractions.Terminology;
using FHIRBridge.Infrastructure.Persistence;
using FHIRBridge.Infrastructure.Terminology;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FHIRBridge.Api.Controllers.V1;

[ApiController]
[Authorize]
[Route("api/v1/terminology/loinc/configuration")]
public sealed class LoincConfigurationController : ControllerBase
{
    private readonly ILoincConfigurationService _service;
    private readonly TerminologyImportChannel _importChannel;
    private readonly FHIRBridgeDbContext _db;
    public LoincConfigurationController(ILoincConfigurationService service, TerminologyImportChannel importChannel, FHIRBridgeDbContext db) =>
        (_service, _importChannel, _db) = (service, importChannel, db);

    [HttpGet]
    [StandardPermission(PermissionGroupCode.Loinc, PermissionActionCode.View, description: "View LOINC terminology configuration.")]
    public async Task<ActionResult<LoincConfigurationDto>> Get(CancellationToken cancellationToken) => Ok(await _service.GetAsync(cancellationToken));

    [HttpPut]
    [StandardPermission(PermissionGroupCode.Loinc, PermissionActionCode.Write, description: "Update LOINC terminology configuration and credentials.")]
    public async Task<ActionResult<LoincConfigurationDto>> Update([FromBody] UpdateLoincConfigurationRequest request, CancellationToken cancellationToken) => Ok(await _service.UpdateAsync(request, cancellationToken));

    [HttpPost("synchronize")]
    [StandardPermission(PermissionGroupCode.Loinc, PermissionActionCode.Write, description: "Manually synchronize the LOINC release.")]
    public IActionResult Synchronize()
    {
        _importChannel.Enqueue(async (services, ct) =>
        {
            var synchronization = services.GetRequiredService<ILoincSynchronizationService>();
            await synchronization.SynchronizeAsync(ct);
        });

        return Accepted(new { message = "LOINC synchronization started in the background. Check import history for progress." });
    }

    [HttpGet("history")]
    [StandardPermission(PermissionGroupCode.Loinc, PermissionActionCode.View, description: "View LOINC import history.")]
    public async Task<ActionResult<IReadOnlyList<LoincImportHistoryEntryDto>>> History(CancellationToken cancellationToken)
    {
        var history = await _db.LoincImportHistory
            .OrderByDescending(x => x.StartedOnUtc)
            .Take(20)
            .Select(x => new LoincImportHistoryEntryDto(x.Id, x.Version, x.StartedOnUtc, x.CompletedOnUtc, x.ImportedConceptCount, x.Status, x.ErrorMessage))
            .ToListAsync(cancellationToken);
        return Ok(history);
    }
}
