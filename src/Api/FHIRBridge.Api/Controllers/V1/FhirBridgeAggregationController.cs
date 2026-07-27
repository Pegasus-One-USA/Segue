using System.Net;
using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Aggregation;
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
/// </summary>
[ApiController]
[Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
[Route("api/v1/fhirbridge")]
public sealed class FhirBridgeAggregationController : ControllerBase
{
    private const string FhirJsonContentType = "application/fhir+json";

    private readonly IPatientAggregationService _aggregationService;

    public FhirBridgeAggregationController(IPatientAggregationService aggregationService)
    {
        _aggregationService = aggregationService;
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
        IReadOnlyList<string> resourceTypes;
        try
        {
            resourceTypes = PatientCompartmentResolver.Resolve(include);
        }
        catch (UnsupportedResourceTypeException ex)
        {
            return FhirError(HttpStatusCode.BadRequest, "not-supported",
                FHIRBridge.Governance.SafeErrorText.SanitizeOr(ex.Message, "One or more requested resource types are not supported."));
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
            // UserMessage is the client-safe text; Message keeps the raw entity/id for logs only.
            return FhirError(HttpStatusCode.NotFound, "not-found", ex.UserMessage);
        }
        catch (SourceConnectionUnavailableException ex)
        {
            return FhirError(HttpStatusCode.Conflict, "conflict",
                FHIRBridge.Governance.SafeErrorText.SanitizeOr(ex.Message, "The source connection is currently unavailable."));
        }

        // Total upstream failure: every query (Patient root + each compartment type) failed.
        var totalQueries = resourceTypes.Count + 1;
        if (result.Resources.Count == 0 && result.Failures.Count >= totalQueries)
        {
            // Per-resource upstream failure messages are untrusted (can carry raw HTTP bodies) — sanitize each.
            var diagnostics = string.Join("; ", result.Failures.Select(f =>
                $"{f.ResourceType}: {FHIRBridge.Governance.SafeErrorText.SanitizeOr(f.Message, "retrieval failed")}"));
            return FhirError(HttpStatusCode.BadGateway, "exception", $"Failed to retrieve any resources from the source. {diagnostics}");
        }

        // Patient root succeeded but returned no Patient — the patient does not exist at the source.
        var patientReturned = result.Resources.Any(r => string.Equals(r.ResourceType, "Patient", StringComparison.OrdinalIgnoreCase));
        var patientQueryFailed = result.Failures.Any(f => string.Equals(f.ResourceType, "Patient", StringComparison.OrdinalIgnoreCase));
        if (!patientReturned && !patientQueryFailed)
        {
            return FhirError(HttpStatusCode.NotFound, "not-found", $"Patient '{id}' was not found.");
        }

        // Sanitize per-resource failure messages at the API boundary before they reach the OperationOutcome — the
        // builder is a building block and must not embed untrusted upstream text (HTML/PHI) into a FHIR response.
        var safeFailures = result.Failures
            .Select(f => f with { Message = FHIRBridge.Governance.SafeErrorText.SanitizeOr(f.Message, "retrieval failed") })
            .ToList();
        var bundleJson = FhirBundleBuilder.Build(result.Resources, safeFailures);
        return Content(bundleJson, FhirJsonContentType);
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
