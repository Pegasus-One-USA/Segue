using System.Net;
using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Aggregation;
using FHIRBridge.Application.Abstractions.Audit;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Fhir;
using FHIRBridge.Integration.Fhir;
using FHIRBridge.SharedKernel.Exceptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

/// <summary>
/// Stateless pass-through export for the provider-standalone flow. The third-party app runs the Epic SMART login
/// itself and POSTs the access token it obtained (plus the FHIR base URL and the patient the clinician selected).
/// FHIRBridge uses that token once to fetch the patient's data and returns it — storing NO token and resolving NO
/// source connection. Phase 1 returns a FHIR <c>searchset</c> Bundle; the admin-configured output format
/// (CSV/JSON/blob/…) is a follow-on that reuses the destination layer (see docs/backend/05). Responses are
/// <c>application/fhir+json</c>; every request emits a PHI-free <c>DataAccess</c> audit event (never the token).
/// </summary>
[ApiController]
[Authorize] // The caller (the provider app) must be authenticated (Entra). Finalize the app-role policy per docs §C7.
[Route("api/v1/tenants/{tenantId:guid}/fhirbridge")]
public sealed class FhirBridgePassthroughController : ControllerBase
{
    private const string FhirJsonContentType = "application/fhir+json";
    private const string AccessActivity = "PassthroughPatientRead";

    private readonly IPassthroughPatientReadService _readService;
    private readonly IPassthroughExportWriter _exportWriter;
    private readonly IUserActivityAuditService _userActivityAuditService;
    private readonly ICurrentUserService _currentUserService;
    private readonly ILogger<FhirBridgePassthroughController> _logger;

    public FhirBridgePassthroughController(
        IPassthroughPatientReadService readService,
        IPassthroughExportWriter exportWriter,
        IUserActivityAuditService userActivityAuditService,
        ICurrentUserService currentUserService,
        ILogger<FhirBridgePassthroughController> logger)
    {
        _readService = readService;
        _exportWriter = exportWriter;
        _userActivityAuditService = userActivityAuditService;
        _currentUserService = currentUserService;
        _logger = logger;
    }

    /// <summary>
    /// <c>POST .../fhirbridge/exports</c> — fetch the patient plus the requested compartment types using the
    /// caller-supplied token. <c>Include</c> defaults to <c>all</c>. Phase 1 returns the merged FHIR Bundle.
    /// </summary>
    [HttpPost("exports")]
    public async Task<IActionResult> Export(
        Guid tenantId,
        [FromBody] PassthroughExportRequest request,
        CancellationToken cancellationToken)
    {
        async Task<IActionResult> Audited(IActionResult result, string status, int resourceCount, string? failureReason)
        {
            await RecordAccessAsync(tenantId, request, status, resourceCount, failureReason, cancellationToken);
            return result;
        }

        if (request is null ||
            string.IsNullOrWhiteSpace(request.AccessToken) ||
            string.IsNullOrWhiteSpace(request.FhirBaseUrl) ||
            string.IsNullOrWhiteSpace(request.PatientId))
        {
            return await Audited(
                FhirError(HttpStatusCode.BadRequest, "invalid", "accessToken, fhirBaseUrl and patientId are required."),
                UserActivityStatuses.Failed, 0, "Missing required fields.");
        }

        IReadOnlyList<string> resourceTypes;
        try
        {
            resourceTypes = PatientCompartmentResolver.Resolve(request.Include);
        }
        catch (UnsupportedResourceTypeException ex)
        {
            return await Audited(
                FhirError(HttpStatusCode.BadRequest, "not-supported", ex.Message),
                UserActivityStatuses.Failed, 0, ex.Message);
        }

        PatientAggregationResult result;
        try
        {
            var context = new CallerTokenReadContext(request.Ehr, request.FhirBaseUrl, request.AccessToken, request.PatientId);
            result = await _readService.GetEverythingAsync(context, resourceTypes, cancellationToken);
        }
        catch (NotSupportedException ex)
        {
            return await Audited(
                FhirError(HttpStatusCode.BadRequest, "not-supported", ex.Message),
                UserActivityStatuses.Failed, 0, ex.Message);
        }
        catch (ArgumentException ex)
        {
            return await Audited(
                FhirError(HttpStatusCode.BadRequest, "invalid", ex.Message),
                UserActivityStatuses.Failed, 0, ex.Message);
        }

        // Total upstream failure: every query (Patient root + each compartment type) failed.
        var totalQueries = resourceTypes.Count + 1;
        if (result.Resources.Count == 0 && result.Failures.Count >= totalQueries)
        {
            var diagnostics = string.Join("; ", result.Failures.Select(f => $"{f.ResourceType}: {f.Message}"));
            return await Audited(
                FhirError(HttpStatusCode.BadGateway, "exception", $"Failed to retrieve any resources from the source. {diagnostics}"),
                UserActivityStatuses.Failed, 0, diagnostics);
        }

        // Patient root succeeded but returned no Patient — the patient does not exist at the source (or is out of the
        // token's context, which the app must have selected during its own SMART login).
        var patientReturned = result.Resources.Any(r => string.Equals(r.ResourceType, "Patient", StringComparison.OrdinalIgnoreCase));
        var patientQueryFailed = result.Failures.Any(f => string.Equals(f.ResourceType, "Patient", StringComparison.OrdinalIgnoreCase));
        if (!patientReturned && !patientQueryFailed)
        {
            return await Audited(
                FhirError(HttpStatusCode.NotFound, "not-found", $"Patient '{request.PatientId}' was not found."),
                UserActivityStatuses.Failed, 0, "Patient not found.");
        }

        var partialFailure = result.Failures.Count > 0
            ? "Partial: " + string.Join(", ", result.Failures.Select(f => f.ResourceType))
            : null;

        // Emit in the tenant's admin-configured format when one is set; otherwise fall back to the FHIR Bundle.
        PassthroughExportResult? export;
        try
        {
            export = await _exportWriter.WriteAsync(tenantId, request.DestinationId, result.Resources, cancellationToken);
        }
        catch (NotFoundException ex)
        {
            return await Audited(
                FhirError(HttpStatusCode.NotFound, "not-found", ex.Message),
                UserActivityStatuses.Failed, 0, ex.Message);
        }

        if (export is not null)
        {
            if (export.Inline)
            {
                if (!string.IsNullOrEmpty(export.FileName))
                {
                    Response.Headers.ContentDisposition = $"attachment; filename=\"{export.FileName}\"";
                }

                return await Audited(
                    Content(export.Content ?? string.Empty, export.ContentType),
                    UserActivityStatuses.Success, export.RecordCount, partialFailure);
            }

            // Sink formats (blob, SQL, S3, …): written to the configured target; return a summary reference.
            return await Audited(
                Ok(new { format = export.Format, recordsWritten = export.WrittenCount, destinationId = request.DestinationId }),
                UserActivityStatuses.Success, export.RecordCount, partialFailure);
        }

        // Fallback: no destination/mapping configured — return the merged FHIR searchset Bundle.
        var bundleJson = FhirBundleBuilder.Build(result.Resources, result.Failures);
        return await Audited(
            Content(bundleJson, FhirJsonContentType),
            UserActivityStatuses.Success, result.Resources.Count, partialFailure);
    }

    private async Task RecordAccessAsync(
        Guid tenantId,
        PassthroughExportRequest? request,
        string status,
        int resourceCount,
        string? failureReason,
        CancellationToken cancellationToken)
    {
        try
        {
            var user = _currentUserService.CurrentUser;
            Guid? userId = Guid.TryParse(user.ExternalUserId, out var parsed) ? parsed : null;
            var httpRequest = HttpContext.Request;

            // PHI-free: records the patient logical id (the access subject) and requested types/counts. NEVER the
            // access token, and never resource content.
            await _userActivityAuditService.RecordAsync(
                new RecordUserActivityRequest(
                    TenantId: tenantId,
                    UserId: userId,
                    UserEmail: user.AuditName,
                    Category: UserActivityCategories.DataAccess,
                    Activity: AccessActivity,
                    Status: status,
                    EntityName: "Patient",
                    EntityId: null,
                    IpAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
                    UserAgent: httpRequest.Headers.UserAgent.ToString(),
                    HttpMethod: httpRequest.Method,
                    RequestPath: httpRequest.Path.Value,
                    Details: $"Ehr={request?.Ehr}; PatientId={request?.PatientId}; Include={request?.Include ?? "all"}; ResourcesReturned={resourceCount}",
                    FailureReason: failureReason,
                    Severity: status == UserActivityStatuses.Success
                        ? UserActivitySeverities.Information
                        : UserActivitySeverities.Warning),
                cancellationToken);
        }
        catch (Exception ex)
        {
            // Auditing must never break the read response; log and continue.
            _logger.LogError(ex, "Failed to record pass-through patient-read access audit.");
        }
    }

    private ContentResult FhirError(HttpStatusCode statusCode, string issueCode, string diagnostics)
    {
        var outcome = new JsonObject
        {
            ["resourceType"] = "OperationOutcome",
            ["issue"] = new JsonArray
            {
                new JsonObject
                {
                    ["severity"] = "error",
                    ["code"] = issueCode,
                    ["diagnostics"] = diagnostics
                }
            }
        };

        return new ContentResult
        {
            Content = outcome.ToJsonString(),
            ContentType = FhirJsonContentType,
            StatusCode = (int)statusCode
        };
    }
}
