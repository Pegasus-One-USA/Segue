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

[ApiController]
[Authorize]
[Route("api/v1/terminology/icd10/configuration")]
public sealed class Icd10ConfigurationController : ControllerBase
{
    // No version-check API exists for ICD-10-CM — NCHS publishes a static FTP directory listing, not a feed.
    // That directory also lists POA-exempt-code lists, guideline PDFs, and addenda alongside the actual release
    // (confirmed live: "icd10cm-Code Descriptions-2026.zip" is the real file; "POAexemptCodesFY26.zip" is not) —
    // both required keywords must match so the freshness check can't latch onto the wrong file.
    private const string ListingUrl = "https://ftp.cdc.gov/pub/Health_Statistics/NCHS/Publications/ICD10CM/2026/";
    private const string SettingsKeyPrefix = "Icd10Cm";
    private static readonly string[] RequiredKeywords = ["icd10cm", "Code"];

    private readonly IReleaseFreshnessChecker _freshnessChecker;
    private readonly TerminologyImportChannel _importChannel;
    private readonly FHIRBridgeDbContext _db;
    public Icd10ConfigurationController(IReleaseFreshnessChecker freshnessChecker, TerminologyImportChannel importChannel, FHIRBridgeDbContext db) =>
        (_freshnessChecker, _importChannel, _db) = (freshnessChecker, importChannel, db);

    [HttpGet("freshness")]
    [StandardPermission(PermissionGroupCode.Icd10, PermissionActionCode.View, description: "View ICD-10-CM release freshness status.")]
    public async Task<IActionResult> GetFreshness(CancellationToken cancellationToken) => Ok(await _freshnessChecker.GetLastCheckAsync(SettingsKeyPrefix, cancellationToken));

    [HttpPost("check-for-updates")]
    [StandardPermission(PermissionGroupCode.Icd10, PermissionActionCode.Write, description: "Check for a newer ICD-10-CM release.")]
    public async Task<IActionResult> CheckForUpdates(CancellationToken cancellationToken) => Ok(await _freshnessChecker.CheckAsync(ListingUrl, SettingsKeyPrefix, RequiredKeywords, cancellationToken));

    [HttpPost("download-and-import")]
    [StandardPermission(PermissionGroupCode.Icd10, PermissionActionCode.Write, description: "Download and import the release identified by the last freshness check.")]
    public async Task<IActionResult> DownloadAndImport(CancellationToken cancellationToken)
    {
        string zipPath;
        try
        {
            zipPath = await _freshnessChecker.DownloadLatestAsync(SettingsKeyPrefix, "Icd10", cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(exception.Message);
        }

        _importChannel.Enqueue(async (services, ct) =>
        {
            var importService = services.GetRequiredService<IIcd10ImportService>();
            await importService.ImportAsync(zipPath, ct);
        });

        return Accepted(new { message = "ICD-10-CM download and import started in the background. Check import history for progress." });
    }

    [HttpPost("import")]
    [RequestSizeLimit(536_870_912)]
    [RequestFormLimits(MultipartBodyLengthLimit = 536_870_912)]
    [StandardPermission(PermissionGroupCode.Icd10, PermissionActionCode.Write, description: "Import an ICD-10-CM release archive.")]
    public async Task<IActionResult> Import(IFormFile file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0) return BadRequest("An ICD-10-CM release .zip file is required.");

        var root = Path.Combine(AppContext.BaseDirectory, "App_Data", "Terminology", "Icd10");
        Directory.CreateDirectory(root);
        var zipPath = Path.Combine(root, $"upload-{Guid.NewGuid():N}.zip");
        await using (var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            await file.CopyToAsync(fileStream, cancellationToken);

        _importChannel.Enqueue(async (services, ct) =>
        {
            var importService = services.GetRequiredService<IIcd10ImportService>();
            await importService.ImportAsync(zipPath, ct);
        });

        return Accepted(new { message = "ICD-10-CM import started in the background. Check import history for progress." });
    }

    [HttpGet("history")]
    [StandardPermission(PermissionGroupCode.Icd10, PermissionActionCode.View, description: "View ICD-10-CM import history.")]
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
