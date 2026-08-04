using FHIRBridge.Api.Security;
using FHIRBridge.Application.Abstractions.Terminology;
using FHIRBridge.Application.Security;
using FHIRBridge.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Api.Controllers.V1;

[ApiController]
[Authorize]
[Route("api/v1/terminology/snomed/configuration")]
public sealed class SnomedConfigurationController : ControllerBase
{
    private readonly ISnomedImportService _importService;
    private readonly FHIRBridgeDbContext _db;
    public SnomedConfigurationController(ISnomedImportService importService, FHIRBridgeDbContext db) => (_importService, _db) = (importService, db);

    [HttpPost("import")]
    [RequestSizeLimit(2_147_483_648)]
    [RequestFormLimits(MultipartBodyLengthLimit = 2_147_483_648)]
    [StandardPermission(PermissionGroupCode.Configuration, PermissionActionCode.Write, description: "Import a SNOMED CT RF2 release archive.")]
    public async Task<ActionResult<SnomedImportResult>> Import(IFormFile file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0) return BadRequest("A SNOMED CT release .zip file is required.");
        await using var stream = file.OpenReadStream();
        return Ok(await _importService.ImportAsync(stream, cancellationToken));
    }

    [HttpGet("history")]
    [StandardPermission(PermissionGroupCode.Configuration, PermissionActionCode.View, description: "View SNOMED CT import history.")]
    public async Task<ActionResult<IReadOnlyList<SnomedImportHistoryEntryDto>>> History(CancellationToken cancellationToken)
    {
        var history = await _db.SnomedImportHistory
            .OrderByDescending(x => x.StartedOnUtc)
            .Take(20)
            .Select(x => new SnomedImportHistoryEntryDto(x.Id, x.Version, x.StartedOnUtc, x.CompletedOnUtc, x.ImportedConceptCount, x.Status, x.ErrorMessage))
            .ToListAsync(cancellationToken);
        return Ok(history);
    }
}
