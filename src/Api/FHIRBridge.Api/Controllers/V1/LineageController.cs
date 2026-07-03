using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

/// <summary>
/// Read-only data-lineage query API. Reconstructs the PHI-free chain of custody (access → normalize → de-identify →
/// output) recorded for resources, optionally filtered by pipeline run, resource type, or source resource id.
/// </summary>
[ApiController]
[Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
[Route("api/v1/lineage")]
public sealed class LineageController : ControllerBase
{
    private readonly ILineageQueryService _lineageQueryService;

    public LineageController(ILineageQueryService lineageQueryService)
    {
        _lineageQueryService = lineageQueryService;
    }

    [HttpGet]
    [ProducesResponseType(typeof(ResourceLineageChain), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetChain(
        [FromQuery] Guid? pipelineRunId,
        [FromQuery] string? resourceType,
        [FromQuery] string? sourceResourceId,
        CancellationToken cancellationToken)
    {
        var chain = await _lineageQueryService.GetChainAsync(
            new LineageQuery(pipelineRunId, resourceType, sourceResourceId),
            cancellationToken);

        return Ok(chain);
    }
}
