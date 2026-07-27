using FHIRBridge.Application.Abstractions.Sources;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

/// <summary>
/// Pre-create endpoint discovery for the source-connection wizard: given only a FHIR base URL, fetch the SMART
/// discovery document (OAuth endpoints + scopes) and the supported resource types, so the wizard can auto-populate
/// those fields before any source connection exists. Post-create discovery stays on <see cref="SourceCapabilitiesController"/>.
/// </summary>
[ApiController]
[Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
[Route("api/v1/source-discovery")]
public sealed class SourceDiscoveryController : ControllerBase
{
    private readonly ISourceEndpointProbeService _probeService;

    public SourceDiscoveryController(ISourceEndpointProbeService probeService)
    {
        _probeService = probeService;
    }

    [HttpPost("probe")]
    [ProducesResponseType(typeof(SourceDiscoveryProbeResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Probe(
        [FromBody] SourceDiscoveryProbeRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request?.BaseUrl))
        {
            return BadRequest("baseUrl is required.");
        }

        // Both probes are best-effort: a plain (non-SMART) FHIR R4 server — e.g. a bare HAPI test instance — has no
        // /.well-known/smart-configuration at all, and some servers gate /metadata behind auth. Either failing
        // shouldn't fail the whole probe; the wizard falls back to manual endpoint entry / the static resource list.
        var smart = SmartConfigurationDto.Empty;
        string? smartConfigurationError = null;
        try
        {
            smart = await _probeService.ProbeSmartConfigurationAsync(request.BaseUrl, cancellationToken);
        }
        catch (Exception ex)
        {
            // Probing an arbitrary external URL — never echo the raw exception/upstream body back to the client.
            smartConfigurationError = FHIRBridge.Governance.SafeErrorText.SanitizeOr(
                ex.Message, "Could not read the endpoint's SMART configuration.");
        }

        IReadOnlyList<string> resourceTypes = [];
        string? resourceTypesError = null;
        try
        {
            resourceTypes = await _probeService.ProbeSupportedResourceTypesAsync(request.BaseUrl, cancellationToken);
        }
        catch (Exception ex)
        {
            resourceTypesError = FHIRBridge.Governance.SafeErrorText.SanitizeOr(
                ex.Message, "Could not read the endpoint's supported resource types.");
        }

        return Ok(new SourceDiscoveryProbeResult(smart, resourceTypes, resourceTypesError, smartConfigurationError));
    }
}
