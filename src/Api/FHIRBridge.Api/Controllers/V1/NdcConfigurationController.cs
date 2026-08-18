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
[Route("api/v1/terminology/ndc/configuration")]
public sealed class NdcConfigurationController : ControllerBase
{
    private readonly INdcConfigurationService _service;
    private readonly TerminologyImportChannel _importChannel;
    private readonly FHIRBridgeDbContext _db;
    public NdcConfigurationController(INdcConfigurationService service, TerminologyImportChannel importChannel, FHIRBridgeDbContext db) =>
        (_service, _importChannel, _db) = (service, importChannel, db);

    [HttpGet]
    [StandardPermission(PermissionGroupCode.Configuration, PermissionActionCode.View, description: "View NDC terminology configuration.")]
    public async Task<ActionResult<NdcConfigurationDto>> Get(CancellationToken cancellationToken) => Ok(await _service.GetAsync(cancellationToken));

    [HttpPut]
    [StandardPermission(PermissionGroupCode.Configuration, PermissionActionCode.Write, description: "Update NDC terminology configuration.")]
    public async Task<ActionResult<NdcConfigurationDto>> Update([FromBody] UpdateNdcConfigurationRequest request, CancellationToken cancellationToken) => Ok(await _service.UpdateAsync(request, cancellationToken));

    [HttpPost("synchronize")]
    [StandardPermission(PermissionGroupCode.Configuration, PermissionActionCode.Write, description: "Manually synchronize the NDC directory.")]
    public IActionResult Synchronize()
    {
        _importChannel.Enqueue(async (services, ct) =>
        {
            var synchronization = services.GetRequiredService<INdcSynchronizationService>();
            await synchronization.SynchronizeAsync(ct);
        });

        return Accepted(new { message = "NDC synchronization started in the background. Check import history for progress." });
    }

    [HttpPost("import")]
    [RequestSizeLimit(2_147_483_648)]
    [RequestFormLimits(MultipartBodyLengthLimit = 2_147_483_648)]
    [StandardPermission(PermissionGroupCode.Configuration, PermissionActionCode.Write, description: "Import an NDC directory export.")]
    public async Task<IActionResult> Import(IFormFile file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0) return BadRequest("An NDC directory export .zip file is required.");

        var root = Path.Combine(AppContext.BaseDirectory, "App_Data", "Terminology", "Ndc");
        Directory.CreateDirectory(root);
        var zipPath = Path.Combine(root, $"upload-{Guid.NewGuid():N}.zip");
        await using (var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            await file.CopyToAsync(fileStream, cancellationToken);

        _importChannel.Enqueue(async (services, ct) =>
        {
            var importService = services.GetRequiredService<INdcImportService>();
            await importService.ImportAsync(zipPath, ct);
        });

        return Accepted(new { message = "NDC import started in the background. Check import history for progress." });
    }

    [HttpGet("history")]
    [StandardPermission(PermissionGroupCode.Configuration, PermissionActionCode.View, description: "View NDC import history.")]
    public async Task<ActionResult<IReadOnlyList<NdcImportHistoryEntryDto>>> History(CancellationToken cancellationToken)
    {
        var history = await _db.NdcImportHistory
            .OrderByDescending(x => x.StartedOnUtc)
            .Take(20)
            .Select(x => new NdcImportHistoryEntryDto(x.Id, x.Version, x.StartedOnUtc, x.CompletedOnUtc, x.ImportedConceptCount, x.Status, x.ErrorMessage))
            .ToListAsync(cancellationToken);
        return Ok(history);
    }
}
