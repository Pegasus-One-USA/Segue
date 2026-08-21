using FHIRBridge.Api.Security;
using FHIRBridge.Application.Abstractions.Terminology;
using FHIRBridge.Application.Security;
using FHIRBridge.Infrastructure.Persistence;
using FHIRBridge.Infrastructure.Terminology;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FHIRBridge.Api.Controllers.V1;

/// <summary>
/// Upload-only for now — CDC references a REST Web Service for CVX codes, but its exact endpoint needs direct
/// verification against live docs before any polling/scheduler code is written against it (see the terminology
/// auto-poll design notes). No <c>check-for-updates</c> endpoint either, unlike ICD-10-PCS/HCPCS, since even the
/// lightweight freshness-scrape approach hasn't been validated against CDC's actual listing page shape yet.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/terminology/cvx/configuration")]
public sealed class CvxConfigurationController : ControllerBase
{
    private readonly TerminologyImportChannel _importChannel;
    private readonly FHIRBridgeDbContext _db;
    public CvxConfigurationController(TerminologyImportChannel importChannel, FHIRBridgeDbContext db) => (_importChannel, _db) = (importChannel, db);

    [HttpPost("import")]
    [RequestSizeLimit(2_147_483_648)]
    [RequestFormLimits(MultipartBodyLengthLimit = 2_147_483_648)]
    [StandardPermission(PermissionGroupCode.Configuration, PermissionActionCode.Write, description: "Import a CVX code-set export.")]
    public async Task<IActionResult> Import(IFormFile file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0) return BadRequest("A CVX code-set export file is required.");

        var root = Path.Combine(AppContext.BaseDirectory, "App_Data", "Terminology", "Cvx");
        Directory.CreateDirectory(root);
        var filePath = Path.Combine(root, $"upload-{Guid.NewGuid():N}{Path.GetExtension(file.FileName)}");
        await using (var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            await file.CopyToAsync(fileStream, cancellationToken);

        _importChannel.Enqueue(async (services, ct) =>
        {
            var importService = services.GetRequiredService<ICvxImportService>();
            await importService.ImportAsync(filePath, ct);
        });

        return Accepted(new { message = "CVX import started in the background. Check import history for progress." });
    }

    [HttpGet("history")]
    [StandardPermission(PermissionGroupCode.Configuration, PermissionActionCode.View, description: "View CVX import history.")]
    public async Task<ActionResult<IReadOnlyList<CvxImportHistoryEntryDto>>> History(CancellationToken cancellationToken)
    {
        var history = await _db.CvxImportHistory
            .OrderByDescending(x => x.StartedOnUtc)
            .Take(20)
            .Select(x => new CvxImportHistoryEntryDto(x.Id, x.Version, x.StartedOnUtc, x.CompletedOnUtc, x.ImportedConceptCount, x.Status, x.ErrorMessage))
            .ToListAsync(cancellationToken);
        return Ok(history);
    }
}
