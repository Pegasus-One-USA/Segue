using FHIRBridge.Api.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Abstractions.Terminology;
using FHIRBridge.Application.Security;
using FHIRBridge.Application.Services;
using FHIRBridge.Infrastructure.Persistence;
using FHIRBridge.Infrastructure.Terminology;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FHIRBridge.Api.Controllers.V1;

[ApiController]
[Authorize]
[Route("api/v1/terminology/ucum/configuration")]
public sealed class UcumConfigurationController : ControllerBase
{
    private readonly IUcumConfigurationService _service;
    private readonly TerminologyImportChannel _importChannel;
    private readonly FHIRBridgeDbContext _db;
    public UcumConfigurationController(IUcumConfigurationService service, TerminologyImportChannel importChannel, FHIRBridgeDbContext db) =>
        (_service, _importChannel, _db) = (service, importChannel, db);

    [HttpGet]
    [StandardPermission(PermissionGroupCode.Configuration, PermissionActionCode.View, description: "View UCUM terminology configuration.")]
    public async Task<ActionResult<UcumConfigurationDto>> Get(CancellationToken cancellationToken) => Ok(await _service.GetAsync(cancellationToken));

    [HttpPut]
    [StandardPermission(PermissionGroupCode.Configuration, PermissionActionCode.Write, description: "Update UCUM terminology configuration.")]
    public async Task<ActionResult<UcumConfigurationDto>> Update([FromBody] UpdateUcumConfigurationRequest request, CancellationToken cancellationToken) => Ok(await _service.UpdateAsync(request, cancellationToken));

    [HttpPost("synchronize")]
    [StandardPermission(PermissionGroupCode.Configuration, PermissionActionCode.Write, description: "Manually synchronize the UCUM unit set.")]
    public IActionResult Synchronize()
    {
        _importChannel.Enqueue(async (services, ct) =>
        {
            var synchronization = services.GetRequiredService<IUcumSynchronizationService>();
            await synchronization.SynchronizeAsync(ct);
        });

        return Accepted(new { message = "UCUM synchronization started in the background. Check import history for progress." });
    }

    [HttpPost("import")]
    [RequestSizeLimit(52_428_800)]
    [StandardPermission(PermissionGroupCode.Configuration, PermissionActionCode.Write, description: "Import a UCUM ucum-essence.xml file.")]
    public async Task<IActionResult> Import(IFormFile file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0) return BadRequest("A ucum-essence.xml file is required.");

        var root = Path.Combine(AppContext.BaseDirectory, "App_Data", "Terminology", "Ucum");
        Directory.CreateDirectory(root);
        var filePath = Path.Combine(root, $"upload-{Guid.NewGuid():N}.xml");
        await using (var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            await file.CopyToAsync(fileStream, cancellationToken);

        _importChannel.Enqueue(async (services, ct) =>
        {
            var importService = services.GetRequiredService<IUcumImportService>();
            await importService.ImportAsync(filePath, ct);
        });

        return Accepted(new { message = "UCUM import started in the background. Check import history for progress." });
    }

    [HttpGet("history")]
    [StandardPermission(PermissionGroupCode.Configuration, PermissionActionCode.View, description: "View UCUM import history.")]
    public async Task<ActionResult<IReadOnlyList<UcumImportHistoryEntryDto>>> History(CancellationToken cancellationToken)
    {
        var history = await _db.UcumImportHistory
            .OrderByDescending(x => x.StartedOnUtc)
            .Take(20)
            .Select(x => new UcumImportHistoryEntryDto(x.Id, x.Version, x.StartedOnUtc, x.CompletedOnUtc, x.ImportedConceptCount, x.Status, x.ErrorMessage))
            .ToListAsync(cancellationToken);
        return Ok(history);
    }
}
