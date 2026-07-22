using FHIRBridge.Api.Security;
using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

/// <summary>Read-only governance log screens (Audit, Authentication, Data Access, Security Events).</summary>
[ApiController]
[Authorize]
[Route("api/v1/governance")]
public sealed class GovernanceController : ControllerBase
{
    private readonly IGovernanceQueryService _governanceQueryService;
    private readonly IComplianceReportService _complianceReportService;

    public GovernanceController(
        IGovernanceQueryService governanceQueryService,
        IComplianceReportService complianceReportService)
    {
        _governanceQueryService = governanceQueryService;
        _complianceReportService = complianceReportService;
    }

    [HttpGet("audit-logs")]
    [StandardPermission(PermissionGroupCode.Governance, PermissionActionCode.Read, description: "View the audit trail.")]
    [ProducesResponseType(typeof(IReadOnlyList<AuditLogDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAuditLogs(
        [FromQuery] string? correlationId,
        [FromQuery] string? entityType,
        [FromQuery] string? entityId,
        [FromQuery] int take,
        CancellationToken cancellationToken)
    {
        var results = await _governanceQueryService.GetAuditLogsAsync(correlationId, entityType, entityId, take, cancellationToken);
        return Ok(results);
    }

    [HttpGet("authentication-logs")]
    [StandardPermission(PermissionGroupCode.Governance, PermissionActionCode.Read, description: "View authentication logs.")]
    [ProducesResponseType(typeof(IReadOnlyList<AuthenticationLogDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAuthenticationLogs(
        [FromQuery] string? correlationId, [FromQuery] int take, [FromQuery] string? authenticationType, CancellationToken cancellationToken)
    {
        var results = await _governanceQueryService.GetAuthenticationLogsAsync(correlationId, take, cancellationToken, authenticationType);
        return Ok(results);
    }

    [HttpGet("data-access-logs")]
    [StandardPermission(PermissionGroupCode.Governance, PermissionActionCode.Read, description: "View patient/resource data access logs.")]
    [ProducesResponseType(typeof(IReadOnlyList<DataAccessLogDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetDataAccessLogs(
        [FromQuery] string? correlationId, [FromQuery] int take, CancellationToken cancellationToken)
    {
        var results = await _governanceQueryService.GetDataAccessLogsAsync(correlationId, take, cancellationToken);
        return Ok(results);
    }

    [HttpGet("security-events")]
    [StandardPermission(PermissionGroupCode.Governance, PermissionActionCode.Read, description: "View security events.")]
    [ProducesResponseType(typeof(IReadOnlyList<SecurityEventDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSecurityEvents(
        [FromQuery] string? correlationId, [FromQuery] int take, CancellationToken cancellationToken)
    {
        var results = await _governanceQueryService.GetSecurityEventsAsync(correlationId, take, cancellationToken);
        return Ok(results);
    }

    [HttpGet("authorization-logs")]
    [StandardPermission(PermissionGroupCode.Governance, PermissionActionCode.Read, description: "View authorization (permission-denial) logs.")]
    [ProducesResponseType(typeof(IReadOnlyList<AuthorizationLogDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAuthorizationLogs(
        [FromQuery] string? correlationId, [FromQuery] int take, CancellationToken cancellationToken)
    {
        var results = await _governanceQueryService.GetAuthorizationLogsAsync(correlationId, take, cancellationToken);
        return Ok(results);
    }

    /// <summary>Correlation Search — everything across every governance/operations table for one CorrelationId.</summary>
    [HttpGet("correlation-search")]
    [StandardPermission(PermissionGroupCode.Governance, PermissionActionCode.Read, description: "Search governance and operations data by correlation ID.")]
    [ProducesResponseType(typeof(CorrelationSearchResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetCorrelationSearchResult(
        [FromQuery] string correlationId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            return BadRequest(new { error = "correlationId is required." });
        }

        var result = await _governanceQueryService.GetCorrelationSearchResultAsync(correlationId, cancellationToken);
        return Ok(result);
    }

    [HttpGet("smart-launch-logs")]
    [StandardPermission(PermissionGroupCode.Governance, PermissionActionCode.Read, description: "View SMART on FHIR launch logs.")]
    [ProducesResponseType(typeof(IReadOnlyList<SmartLaunchLogDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSmartLaunchLogs([FromQuery] int take, CancellationToken cancellationToken)
    {
        var results = await _governanceQueryService.GetSmartLaunchLogsAsync(take, cancellationToken);
        return Ok(results);
    }

    [HttpGet("retention-policies")]
    [StandardPermission(PermissionGroupCode.Governance, PermissionActionCode.Read, description: "View the currently-effective retention policy for every log type.")]
    [ProducesResponseType(typeof(IReadOnlyList<RetentionPolicyDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRetentionPolicies(CancellationToken cancellationToken)
    {
        var results = await _governanceQueryService.GetRetentionPoliciesAsync(cancellationToken);
        return Ok(results);
    }

    /// <summary>
    /// Read-only: what's actually enforced today, not a configuration form. PHI masking has no disable switch by
    /// design; payload logging has no per-tenant opt-in yet — both are reported honestly rather than as fake toggles.
    /// </summary>
    [HttpGet("log-settings")]
    [StandardPermission(PermissionGroupCode.Governance, PermissionActionCode.Read, description: "View the currently-enforced logging configuration.")]
    [ProducesResponseType(typeof(LogSettingsDto), StatusCodes.Status200OK)]
    public IActionResult GetLogSettings()
    {
        var categories = new List<LogCategorySettingDto>
        {
            new("Audit", "AuditLog (hash-chained)", "Every config/entity Created/Updated/Deleted — captured automatically, zero call-site changes."),
            new("Data Access", "DataAccessLog", "Patient/resource governance allow/deny decisions, PHI-free."),
            new("Authentication", "AuthenticationLog (hash-chained)", "Local login/logout/MFA/lockout, SSO, and OAuth token issuance/refresh."),
            new("Authorization", "AuthorizationLog", "RBAC permission denials, written from a single authorization-middleware hook."),
            new("SMART Launch", "SmartLaunchLog (hash-chained)", "SMART on FHIR EHR/Standalone/Workflow launch completion."),
            new("Security Event", "SecurityEvent", "Lockout thresholds, anomalies, audit-chain-broken alerts."),
            new("Scheduler", "SchedulerHistory", "Scheduler dispatch decisions."),
            new("Retry", "RetryHistory", "Retry attempts against transient failures."),
            new("Error", "ErrorLog", "Unhandled exceptions, captured centrally."),
            new("API Request", "ApiRequestLog", "Every outbound HTTP call — method/URL/status/duration only, never headers/tokens/bodies."),
            new("Export", "ExportHistory", "Destination writes completing, across all 21 destination types."),
            new("Notification", "NotificationHistory", "Outbound notifications (Email is the only real channel today)."),
            new("Validation Failure", "ValidationFailureLog", "US-Core/data-quality normalization warnings, PHI-free."),
            new("Endpoint Health", "EndpointHealthCheck", "Periodic connectivity checks — source connections only today."),
        };

        var settings = new LogSettingsDto(
            PhiMaskingEnabled: true,
            PayloadLoggingImplemented: false,
            Categories: categories);

        return Ok(settings);
    }

    [HttpGet("archives")]
    [StandardPermission(PermissionGroupCode.Governance, PermissionActionCode.Read, description: "View archive-before-purge manifests.")]
    [ProducesResponseType(typeof(IReadOnlyList<ArchiveManifestDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetArchives(CancellationToken cancellationToken)
    {
        var results = await _governanceQueryService.GetArchiveManifestsAsync(cancellationToken);
        return Ok(results);
    }

    /// <summary>
    /// Restoring archived rows back into the live tables is not implemented in this pass — the archive artifact
    /// (NDJSON file, see <see cref="ArchiveManifestDto.FileLocation"/>) is retrievable and durable, but there is
    /// no automated re-insert path yet. Returns 501 rather than pretending to succeed.
    /// </summary>
    [HttpPost("archives/{dataClass}/restore")]
    [StandardPermission(PermissionGroupCode.Governance, PermissionActionCode.Read, description: "Restore an archived log range (not yet implemented).")]
    [ProducesResponseType(StatusCodes.Status501NotImplemented)]
    public IActionResult RestoreArchive(string dataClass)
    {
        return StatusCode(StatusCodes.Status501NotImplemented, new
        {
            error = $"Restoring archived {dataClass} rows is not implemented yet. The archive file itself is durable and retrievable — see the FileLocation from GET /governance/archives.",
        });
    }

    /// <summary>Generates and downloads the HIPAA/SOC2 compliance report PDF for the given UTC date range.</summary>
    [HttpGet("reports/hipaa-audit")]
    [StandardPermission(PermissionGroupCode.Governance, PermissionActionCode.Read, description: "Generate the HIPAA/SOC2 compliance report.")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetHipaaAuditReport(
        [FromQuery] DateTime from, [FromQuery] DateTime to, CancellationToken cancellationToken)
    {
        var fromUtc = DateTime.SpecifyKind(from.Date, DateTimeKind.Utc);
        var toUtc = DateTime.SpecifyKind(to.Date, DateTimeKind.Utc).AddDays(1).AddTicks(-1);

        var pdfBytes = await _complianceReportService.GenerateHipaaAuditReportAsync(fromUtc, toUtc, cancellationToken);

        return File(pdfBytes, "application/pdf", $"Segue-Compliance-Report-{fromUtc:yyyyMMdd}-{toUtc:yyyyMMdd}.pdf");
    }

    /// <summary>Generates and downloads the SOC2 evidence export PDF for the given UTC date range — a distinct
    /// artifact from the HIPAA report, manual-trigger-only (no scheduled monthly generation exists yet).</summary>
    [HttpGet("reports/soc2-evidence")]
    [StandardPermission(PermissionGroupCode.Governance, PermissionActionCode.Read, description: "Generate the SOC2 evidence export.")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSoc2EvidenceReport(
        [FromQuery] DateTime from, [FromQuery] DateTime to, CancellationToken cancellationToken)
    {
        var fromUtc = DateTime.SpecifyKind(from.Date, DateTimeKind.Utc);
        var toUtc = DateTime.SpecifyKind(to.Date, DateTimeKind.Utc).AddDays(1).AddTicks(-1);

        var pdfBytes = await _complianceReportService.GenerateSoc2EvidenceReportAsync(fromUtc, toUtc, cancellationToken);

        return File(pdfBytes, "application/pdf", $"Segue-SOC2-Evidence-{fromUtc:yyyyMMdd}-{toUtc:yyyyMMdd}.pdf");
    }
}
