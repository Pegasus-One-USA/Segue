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
[Route("api/v1/terminology/icd10/configuration")]
public sealed class Icd10ConfigurationController : ControllerBase
{
    private readonly IIcd10ImportService _importService;
    private readonly FHIRBridgeDbContext _db;
    public Icd10ConfigurationController(IIcd10ImportService importService, FHIRBridgeDbContext db) => (_importService, _db) = (importService, db);

    [HttpPost("import")]
    [RequestSizeLimit(536_870_912)]
    [RequestFormLimits(MultipartBodyLengthLimit = 536_870_912)]
    [StandardPermission(PermissionGroupCode.Configuration, PermissionActionCode.Write, description: "Import an ICD-10-CM release archive.")]
    public async Task<ActionResult<Icd10ImportResult>> Import(IFormFile file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0) return BadRequest("An ICD-10-CM release .zip file is required.");
        await using var stream = file.OpenReadStream();
        return Ok(await _importService.ImportAsync(stream, cancellationToken));
    }

    [HttpGet("history")]
    [StandardPermission(PermissionGroupCode.Configuration, PermissionActionCode.View, description: "View ICD-10-CM import history.")]
    public async Task<ActionResult<IReadOnlyList<Icd10ImportHistoryEntryDto>>> History(CancellationToken cancellationToken)
    {
        var history = await _db.Icd10ImportHistory
            .OrderByDescending(x => x.StartedOnUtc)
            .Take(20)
            .Select(x => new Icd10ImportHistoryEntryDto(x.Id, x.Version, x.StartedOnUtc, x.CompletedOnUtc, x.ImportedCodeCount, x.Status, x.ErrorMessage))
            .ToListAsync(cancellationToken);
        return Ok(history);
    }
}
