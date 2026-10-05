using FHIRBridge.Api.Security;
using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Application.Security;
using FHIRBridge.Governance;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace FHIRBridge.Api.Controllers.V1;

public sealed record ErrorLogPurgeResult(int Deleted, DateTime CutoffUtc);

/// <summary>"Error log handling" settings: what is captured, how long it is kept, auto-clear, and the log's size.</summary>
[ApiController]
[Authorize]
[Route("api/v1/operations/error-log-settings")]
public sealed class ErrorLogSettingsController : ControllerBase
{
    private readonly IErrorLogSettingsStore _store;
    private readonly IErrorLogMaintenanceService _maintenance;
    private readonly IErrorCapturePolicy _policy;
    private readonly IGovernanceLogger _governanceLogger;
    private readonly int _minimumRetentionDays;
    private readonly IAuthorizationService _authorization;

    public ErrorLogSettingsController(
        IErrorLogSettingsStore store, IErrorLogMaintenanceService maintenance, IErrorCapturePolicy policy,
        IGovernanceLogger governanceLogger, IOptions<ErrorCaptureOptions> captureOptions,
        IAuthorizationService authorization)
    {
        _authorization = authorization;
        _minimumRetentionDays = Math.Max(0, captureOptions.Value.MinimumRetentionDays);
        _governanceLogger = governanceLogger;
        _store = store;
        _maintenance = maintenance;
        _policy = policy;
    }

    [HttpGet]
    [StandardPermission(PermissionGroupCode.Governance, PermissionActionCode.Read, description: "View the error log settings.")]
    [ProducesResponseType(typeof(ErrorLogSettingsResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken) =>
        Ok(await BuildResponseAsync(await _store.GetAsync(cancellationToken), cancellationToken));

    [HttpPut]
    [StandardPermission(PermissionGroupCode.Governance, PermissionActionCode.Write, description: "Change the error log settings.")]
    [ProducesResponseType(typeof(ErrorLogSettingsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Save([FromBody] ErrorLogSettings request, CancellationToken cancellationToken)
    {
        var settings = request.Normalize();
        if (settings.CaptureSeverities.Count == 0)
        {
            return BadRequest(new { error = "Choose at least one type of entry to capture." });
        }

        if (settings.CaptureCategories.Count == 0)
        {
            return BadRequest(new { error = "Choose at least one error category to capture." });
        }

        if (settings.RetentionDays < _minimumRetentionDays)
        {
            return BadRequest(new { error = $"Entries must be kept for at least {_minimumRetentionDays} days (set by the deployment)." });
        }

        // Retention / auto-clear decide when entries are permanently deleted (by the hourly worker, with no further check),
        // so switching auto-clear on or shortening retention needs the same permission as deleting.
        var before = await _store.GetAsync(cancellationToken);
        var weakensRetention =
            (settings.AutoClearEnabled && !before.AutoClearEnabled)
            || (settings.AutoClearEnabled && settings.RetentionDays < before.RetentionDays);
        if (weakensRetention
            && !(await _authorization.AuthorizeAsync(User, "HasPermission:governance.delete")).Succeeded)
        {
            return Forbid();
        }

        await _store.SaveAsync(settings, cancellationToken);
        await _governanceLogger.LogSecurityEventAsync(
            new SecurityEventEntry(
                "ErrorLogSettingsChanged", "Information", User.Identity?.Name,
                $"Error log settings changed. Auto-clear {(before.AutoClearEnabled ? "on" : "off")} -> {(settings.AutoClearEnabled ? "on" : "off")}; "
                + $"retention {before.RetentionDays} -> {settings.RetentionDays} day(s); "
                + $"entry types [{string.Join(", ", settings.CaptureSeverities)}]; categories {settings.CaptureCategories.Count}; "
                + $"workflow trace detail {settings.WorkflowDebugDetail}."),
            cancellationToken);
        await _policy.RefreshAsync(cancellationToken);
        return Ok(await BuildResponseAsync(settings, cancellationToken));
    }

    /// <summary>Just the size figures (shown on the Error Dashboard).</summary>
    [HttpGet("storage")]
    [StandardPermission(PermissionGroupCode.Governance, PermissionActionCode.Read, description: "View the error log size.")]
    [ProducesResponseType(typeof(ErrorLogStorageDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStorage(CancellationToken cancellationToken) =>
        Ok(await _maintenance.GetStorageAsync(cancellationToken));

    /// <summary>Deletes entries older than the configured retention period right now (the same thing auto-clear does
    /// hourly), whether or not auto-clear is switched on.</summary>
    [HttpPost("purge")]
    [StandardPermission(PermissionGroupCode.Governance, PermissionActionCode.Delete, description: "Delete old error log entries now.")]
    [ProducesResponseType(typeof(ErrorLogPurgeResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> Purge(CancellationToken cancellationToken)
    {
        var settings = await _store.GetAsync(cancellationToken);
        var cutoff = DateTime.UtcNow.AddDays(-Math.Max(settings.RetentionDays, _minimumRetentionDays));
        // Audit first (see OperationsController.DeleteErrors): no irreversible delete without a record of the request.
        await _governanceLogger.LogSecurityEventAsync(
            new SecurityEventEntry(
                "ErrorLogDeleteRequested", "Warning", User.Identity?.Name,
                $"Permanent delete requested from the settings screen: entries before {cutoff:u}."),
            cancellationToken);

        var deleted = await _maintenance.DeleteOlderThanAsync(cutoff, cancellationToken);
        try
        {
            await _governanceLogger.LogSecurityEventAsync(
                new SecurityEventEntry(
                    "ErrorLogDeleted", "Warning", User.Identity?.Name,
                    $"Entries before {cutoff:u} deleted from the settings screen: {deleted} removed."),
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            SwallowedError.Report(exception, "ErrorLog.purge completion audit");
        }

        return Ok(new ErrorLogPurgeResult(deleted, cutoff));
    }

    private async Task<ErrorLogSettingsResponse> BuildResponseAsync(ErrorLogSettings settings, CancellationToken cancellationToken) =>
        new(settings, await _maintenance.GetStorageAsync(cancellationToken), ErrorLogSettings.AllSeverities, ErrorLogSettings.AllCategories, _minimumRetentionDays);
}
