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
    private readonly IFieldLineageQueryService _fieldLineageQueryService;

    public LineageController(ILineageQueryService lineageQueryService, IFieldLineageQueryService fieldLineageQueryService)
    {
        _lineageQueryService = lineageQueryService;
        _fieldLineageQueryService = fieldLineageQueryService;
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

    // Field-level drill-down for one resource — "why does this destination column hold this value." Empty unless
    // FieldLineage:Enabled was on (and a Mapping node actually ran) for the run(s) that touched this resource; the
    // resource-level chain above is what tells the caller a resource exists to drill into in the first place.
    [HttpGet("fields")]
    [StandardPermission(PermissionGroupCode.AuditLogs, PermissionActionCode.Read, description: "View field-level resource lineage.")]
    [ProducesResponseType(typeof(IReadOnlyList<FieldLineageRecord>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetFields(
        [FromQuery] string resourceType,
        [FromQuery] string sourceResourceId,
        [FromQuery] Guid? pipelineRunId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(resourceType) || string.IsNullOrWhiteSpace(sourceResourceId))
        {
            return BadRequest(new { error = "invalid_request", error_description = "resourceType and sourceResourceId are required." });
        }

        var fields = await _fieldLineageQueryService.GetFieldsAsync(
            new FieldLineageQuery(resourceType, sourceResourceId, pipelineRunId),
            cancellationToken);

        return Ok(fields);
    }
}
