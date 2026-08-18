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
[Route("api/v1/terminology/hcpcs/configuration")]
public sealed class HcpcsConfigurationController : ControllerBase
{
    // No version-check API exists for HCPCS Level II — CMS publishes a static quarterly-update page, not a feed.
    // Historical CMS naming convention is "HCPC{year}_{quarter}_ANWEB.zip" — this keyword is broad/best-effort;
    // verify against the live page before relying on this in production, same caveat as ICD-10-PCS.
    private const string ListingUrl = "https://www.cms.gov/medicare/coding-billing/healthcare-common-procedure-system/quarterly-update";
    private const string SettingsKeyPrefix = "Hcpcs";
    private static readonly string[] RequiredKeywords = ["HCPC"];

    private readonly IReleaseFreshnessChecker _freshnessChecker;
    private readonly TerminologyImportChannel _importChannel;
    private readonly FHIRBridgeDbContext _db;

    public HcpcsConfigurationController(IReleaseFreshnessChecker freshnessChecker, TerminologyImportChannel importChannel, FHIRBridgeDbContext db) =>
        (_freshnessChecker, _importChannel, _db) = (freshnessChecker, importChannel, db);

    [HttpGet("freshness")]
    [StandardPermission(PermissionGroupCode.Configuration, PermissionActionCode.View, description: "View HCPCS release freshness status.")]
    public async Task<IActionResult> GetFreshness(CancellationToken cancellationToken) => Ok(await _freshnessChecker.GetLastCheckAsync(SettingsKeyPrefix, cancellationToken));

    [HttpPost("check-for-updates")]
    [StandardPermission(PermissionGroupCode.Configuration, PermissionActionCode.Write, description: "Check for a newer HCPCS release.")]
    public async Task<IActionResult> CheckForUpdates(CancellationToken cancellationToken) => Ok(await _freshnessChecker.CheckAsync(ListingUrl, SettingsKeyPrefix, RequiredKeywords, cancellationToken));

    [HttpPost("download-and-import")]
    [StandardPermission(PermissionGroupCode.Configuration, PermissionActionCode.Write, description: "Download and import the release identified by the last freshness check.")]
    public async Task<IActionResult> DownloadAndImport(CancellationToken cancellationToken)
    {
        string zipPath;
        try
        {
            zipPath = await _freshnessChecker.DownloadLatestAsync(SettingsKeyPrefix, "Hcpcs", cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(exception.Message);
        }

        _importChannel.Enqueue(async (services, ct) =>
        {
            var importService = services.GetRequiredService<IHcpcsImportService>();
            await importService.ImportAsync(zipPath, ct);
        });

        return Accepted(new { message = "HCPCS download and import started in the background. Check import history for progress." });
    }

    [HttpPost("import")]
    [RequestSizeLimit(2_147_483_648)]
    [RequestFormLimits(MultipartBodyLengthLimit = 2_147_483_648)]
    [StandardPermission(PermissionGroupCode.Configuration, PermissionActionCode.Write, description: "Import a HCPCS release archive.")]
    public async Task<IActionResult> Import(IFormFile file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0) return BadRequest("A HCPCS release .zip file is required.");

        var root = Path.Combine(AppContext.BaseDirectory, "App_Data", "Terminology", "Hcpcs");
        Directory.CreateDirectory(root);
        var zipPath = Path.Combine(root, $"upload-{Guid.NewGuid():N}.zip");
        await using (var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            await file.CopyToAsync(fileStream, cancellationToken);

        _importChannel.Enqueue(async (services, ct) =>
        {
            var importService = services.GetRequiredService<IHcpcsImportService>();
            await importService.ImportAsync(zipPath, ct);
        });

        return Accepted(new { message = "HCPCS import started in the background. Check import history for progress." });
    }

    [HttpGet("history")]
    [StandardPermission(PermissionGroupCode.Configuration, PermissionActionCode.View, description: "View HCPCS import history.")]
    public async Task<ActionResult<IReadOnlyList<HcpcsImportHistoryEntryDto>>> History(CancellationToken cancellationToken)
    {
        var history = await _db.HcpcsImportHistory
            .OrderByDescending(x => x.StartedOnUtc)
            .Take(20)
            .Select(x => new HcpcsImportHistoryEntryDto(x.Id, x.Version, x.StartedOnUtc, x.CompletedOnUtc, x.ImportedConceptCount, x.Status, x.ErrorMessage))
            .ToListAsync(cancellationToken);
        return Ok(history);
    }
}
