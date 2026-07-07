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

        // SMART discovery is authoritative (fail the call if it can't be read). Resource types are best-effort — some
        // servers gate /metadata behind auth, so surface that as a message rather than failing the whole probe.
        var smart = await _probeService.ProbeSmartConfigurationAsync(request.BaseUrl, cancellationToken);

        IReadOnlyList<string> resourceTypes = [];
        string? resourceTypesError = null;
        try
        {
            resourceTypes = await _probeService.ProbeSupportedResourceTypesAsync(request.BaseUrl, cancellationToken);
        }
        catch (Exception ex)
        {
            resourceTypesError = ex.Message;
        }

        return Ok(new SourceDiscoveryProbeResult(smart, resourceTypes, resourceTypesError));
    }
}
