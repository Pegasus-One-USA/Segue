using System.Net;
using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Aggregation;
using FHIRBridge.Application.Abstractions.Audit;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using FHIRBridge.Domain.Fhir;
using FHIRBridge.Integration.Fhir;
using FHIRBridge.SharedKernel.Exceptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

/// <summary>
/// Synchronous, patient-scoped FHIR aggregation read. Fetches a Patient plus a caller-selected set of
/// patient-compartment resource types and returns them as a single <c>searchset</c> Bundle. Pass-through read:
/// no mapping, no destination, no PipelineRun. Responses (success and error) are <c>application/fhir+json</c>.
/// Every request emits a PHI-free <c>DataAccess</c> user-activity audit event (who/what/when/where/outcome).
/// </summary>
[ApiController]
[Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
[Route("api/v1/fhirbridge")]
public sealed class FhirBridgeAggregationController : ControllerBase
{
    private const string FhirJsonContentType = "application/fhir+json";
    private const string AccessActivity = "PatientAggregationRead";

    private readonly IPatientAggregationService _aggregationService;
    private readonly IUserActivityAuditService _userActivityAuditService;
    private readonly ICurrentUserService _currentUserService;
    private readonly ILogger<FhirBridgeAggregationController> _logger;

    public FhirBridgeAggregationController(
        IPatientAggregationService aggregationService,
        IUserActivityAuditService userActivityAuditService,
        ICurrentUserService currentUserService,
        ILogger<FhirBridgeAggregationController> logger)
    {
        _aggregationService = aggregationService;
        _userActivityAuditService = userActivityAuditService;
        _currentUserService = currentUserService;
        _logger = logger;
    }

    /// <summary>
    /// <c>GET .../fhirbridge/Patient/{id}?include=all|Type,Type&amp;source={sourceConnectionId}</c>.
    /// <paramref name="include"/> defaults to <c>all</c> when omitted. <paramref name="source"/> is required only
    /// when there is more than one enabled source connection.
    /// </summary>
    [HttpGet("Patient/{id}")]
    [Produces(FhirJsonContentType)]
    public async Task<IActionResult> GetPatientEverything(
        string id,
        [FromQuery] string? include,
        [FromQuery] Guid? source,
        CancellationToken cancellationToken)
    {
        async Task<IActionResult> Audited(
            IActionResult result,
            string status,
            int resourceCount,
            string? failureReason)
        {
            await RecordAccessAsync(id, include, status, resourceCount, failureReason, cancellationToken);
            return result;
        }

        IReadOnlyList<string> resourceTypes;
        try
        {
            resourceTypes = PatientCompartmentResolver.Resolve(include);
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
            result = await _aggregationService.GetEverythingAsync(
                id,
                resourceTypes,
                source,
                cancellationToken);
        }
        catch (NotFoundException ex)
        {
            return await Audited(
                FhirError(HttpStatusCode.NotFound, "not-found", ex.Message),
                UserActivityStatuses.Failed, 0, ex.Message);
        }
        catch (SourceConnectionUnavailableException ex)
        {
            return await Audited(
                FhirError(HttpStatusCode.Conflict, "conflict", ex.Message),
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

        // Patient root succeeded but returned no Patient — the patient does not exist at the source.
        var patientReturned = result.Resources.Any(r => string.Equals(r.ResourceType, "Patient", StringComparison.OrdinalIgnoreCase));
        var patientQueryFailed = result.Failures.Any(f => string.Equals(f.ResourceType, "Patient", StringComparison.OrdinalIgnoreCase));
        if (!patientReturned && !patientQueryFailed)
        {
            return await Audited(
                FhirError(HttpStatusCode.NotFound, "not-found", $"Patient '{id}' was not found."),
                UserActivityStatuses.Failed, 0, "Patient not found.");
        }

        var bundleJson = FhirBundleBuilder.Build(result.Resources, result.Failures);
        var partialFailure = result.Failures.Count > 0
            ? "Partial: " + string.Join(", ", result.Failures.Select(f => f.ResourceType))
            : null;
        return await Audited(
            Content(bundleJson, FhirJsonContentType),
            UserActivityStatuses.Success, result.Resources.Count, partialFailure);
    }

    private async Task RecordAccessAsync(
        string patientId,
        string? include,
        string status,
        int resourceCount,
        string? failureReason,
        CancellationToken cancellationToken)
    {
        try
        {
            var user = _currentUserService.CurrentUser;
            Guid? userId = Guid.TryParse(user.ExternalUserId, out var parsed) ? parsed : null;
            var request = HttpContext.Request;

            // PHI-free: records the patient logical id (the access subject) and requested types/counts, never resource content.
            await _userActivityAuditService.RecordAsync(
                new RecordUserActivityRequest(
                    UserId: userId,
                    UserEmail: user.AuditName,
                    Category: UserActivityCategories.DataAccess,
                    Activity: AccessActivity,
                    Status: status,
                    EntityName: "Patient",
                    EntityId: null,
                    IpAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
                    UserAgent: request.Headers.UserAgent.ToString(),
                    HttpMethod: request.Method,
                    RequestPath: request.Path.Value,
                    Details: $"PatientId={patientId}; Include={include ?? "all"}; ResourcesReturned={resourceCount}",
                    FailureReason: failureReason,
                    Severity: status == UserActivityStatuses.Success
                        ? UserActivitySeverities.Information
                        : UserActivitySeverities.Warning),
                cancellationToken);
        }
        catch (Exception ex)
        {
            // Auditing must never break the read response; log and continue.
            _logger.LogError(ex, "Failed to record patient-aggregation access audit for patient {PatientId}.", patientId);
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
