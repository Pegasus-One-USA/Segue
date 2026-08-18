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
[Route("api/v1/terminology/icd10pcs/configuration")]
public sealed class Icd10PcsConfigurationController : ControllerBase
{
    // No version-check API exists for ICD-10-PCS — CMS publishes a static listing page, not a versioned feed.
    // Confirmed live: the real 2026 file is "april-1-2026-icd-10-pcs-order-file-long-abbreviated-titles.zip" —
    // hyphenated "icd-10-pcs", NOT "icd10pcs" (an earlier version of this filter used the wrong keyword and
    // never matched anything). The page also lists ICD-10-CM files, addenda, code-tables-and-index, and
    // conversion-table archives alongside the actual Order File — both required keywords must match so this
    // can't latch onto one of those instead.
    private const string ListingUrl = "https://www.cms.gov/medicare/coding-billing/icd-10-codes";
    private const string SettingsKeyPrefix = "Icd10Pcs";
    private static readonly string[] RequiredKeywords = ["icd-10-pcs", "order"];

    private readonly IReleaseFreshnessChecker _freshnessChecker;
    private readonly TerminologyImportChannel _importChannel;
    private readonly FHIRBridgeDbContext _db;

    public Icd10PcsConfigurationController(IReleaseFreshnessChecker freshnessChecker, TerminologyImportChannel importChannel, FHIRBridgeDbContext db) =>
        (_freshnessChecker, _importChannel, _db) = (freshnessChecker, importChannel, db);

    [HttpGet("freshness")]
    [StandardPermission(PermissionGroupCode.Configuration, PermissionActionCode.View, description: "View ICD-10-PCS release freshness status.")]
    public async Task<IActionResult> GetFreshness(CancellationToken cancellationToken) => Ok(await _freshnessChecker.GetLastCheckAsync(SettingsKeyPrefix, cancellationToken));

    [HttpPost("check-for-updates")]
    [StandardPermission(PermissionGroupCode.Configuration, PermissionActionCode.Write, description: "Check for a newer ICD-10-PCS release.")]
    public async Task<IActionResult> CheckForUpdates(CancellationToken cancellationToken) => Ok(await _freshnessChecker.CheckAsync(ListingUrl, SettingsKeyPrefix, RequiredKeywords, cancellationToken));

    [HttpPost("download-and-import")]
    [StandardPermission(PermissionGroupCode.Configuration, PermissionActionCode.Write, description: "Download and import the release identified by the last freshness check.")]
    public async Task<IActionResult> DownloadAndImport(CancellationToken cancellationToken)
    {
        string zipPath;
        try
        {
            zipPath = await _freshnessChecker.DownloadLatestAsync(SettingsKeyPrefix, "Icd10Pcs", cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(exception.Message);
        }

        _importChannel.Enqueue(async (services, ct) =>
        {
            var importService = services.GetRequiredService<IIcd10PcsImportService>();
            await importService.ImportAsync(zipPath, ct);
        });

        return Accepted(new { message = "ICD-10-PCS download and import started in the background. Check import history for progress." });
    }

    [HttpPost("import")]
    [RequestSizeLimit(2_147_483_648)]
    [RequestFormLimits(MultipartBodyLengthLimit = 2_147_483_648)]
    [StandardPermission(PermissionGroupCode.Configuration, PermissionActionCode.Write, description: "Import an ICD-10-PCS release archive.")]
    public async Task<IActionResult> Import(IFormFile file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0) return BadRequest("An ICD-10-PCS release .zip file is required.");

        var root = Path.Combine(AppContext.BaseDirectory, "App_Data", "Terminology", "Icd10Pcs");
        Directory.CreateDirectory(root);
        var zipPath = Path.Combine(root, $"upload-{Guid.NewGuid():N}.zip");
        await using (var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            await file.CopyToAsync(fileStream, cancellationToken);

        _importChannel.Enqueue(async (services, ct) =>
        {
            var importService = services.GetRequiredService<IIcd10PcsImportService>();
            await importService.ImportAsync(zipPath, ct);
        });

        return Accepted(new { message = "ICD-10-PCS import started in the background. Check import history for progress." });
    }

    [HttpGet("history")]
    [StandardPermission(PermissionGroupCode.Configuration, PermissionActionCode.View, description: "View ICD-10-PCS import history.")]
    public async Task<ActionResult<IReadOnlyList<Icd10PcsImportHistoryEntryDto>>> History(CancellationToken cancellationToken)
    {
        var history = await _db.Icd10PcsImportHistory
            .OrderByDescending(x => x.StartedOnUtc)
            .Take(20)
            .Select(x => new Icd10PcsImportHistoryEntryDto(x.Id, x.Version, x.StartedOnUtc, x.CompletedOnUtc, x.ImportedConceptCount, x.Status, x.ErrorMessage))
            .ToListAsync(cancellationToken);
        return Ok(history);
    }
}
