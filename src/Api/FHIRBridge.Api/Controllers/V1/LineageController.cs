using FHIRBridge.Api.Security;
using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

/// <summary>
/// Read-only data-lineage query API. Reconstructs the PHI-free chain of custody (access → normalize → de-identify →
/// output) recorded for resources, optionally filtered by pipeline run, resource type, or source resource id.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/lineage")]
public sealed class LineageController : ControllerBase
{
    private readonly ILineageQueryService _lineageQueryService;

    public LineageController(ILineageQueryService lineageQueryService)
    {
        _lineageQueryService = lineageQueryService;
    }

    [HttpGet]
    [StandardPermission(PermissionGroupCode.AuditLogs, PermissionActionCode.Read, description: "View resource lineage (chain of custody).")]
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

    [HttpGet("entries")]
    [StandardPermission(PermissionGroupCode.AuditLogs, PermissionActionCode.Read, description: "View resource lineage (chain of custody).")]
    [ProducesResponseType(typeof(PagedResult<ResourceLineageRecord>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetEntries(
        [FromQuery] Guid? pipelineRunId,
        [FromQuery] string? resourceType,
        [FromQuery] string? action,
        [FromQuery] string? status,
        [FromQuery] int page,
        [FromQuery] int pageSize,
        CancellationToken cancellationToken)
    {
        var result = await _lineageQueryService.GetPagedAsync(
            new LineageListFilter(pipelineRunId, resourceType, action, status),
            page <= 0 ? 1 : page,
            pageSize <= 0 ? 25 : pageSize,
            cancellationToken);

        return Ok(result);
    }
}
