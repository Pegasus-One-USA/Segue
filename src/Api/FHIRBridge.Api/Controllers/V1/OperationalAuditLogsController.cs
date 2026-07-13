using FHIRBridge.Api.Security;
using FHIRBridge.Application.Abstractions.Audit;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

[ApiController]
[Authorize]
[Route("api/v1/audit-logs")]
public sealed class OperationalAuditLogsController : ControllerBase
{
    private readonly IOperationalAuditService _auditService;

    public OperationalAuditLogsController(IOperationalAuditService auditService)
    {
        _auditService = auditService;
    }

    [HttpGet]
    [StandardPermission(PermissionGroupCode.AuditLogs, PermissionActionCode.Read, description: "View operational/pipeline audit logs.")]
    [ProducesResponseType(typeof(IReadOnlyList<OperationalAuditLogDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRecent(
        [FromQuery] int count,
        CancellationToken cancellationToken)
    {
        var auditLogs = await _auditService.GetRecentAsync(
            count <= 0 ? 100 : count,
            cancellationToken);

        return Ok(auditLogs);
    }

    [HttpGet("paged")]
    [StandardPermission(PermissionGroupCode.AuditLogs, PermissionActionCode.Read, description: "View operational/pipeline audit logs.")]
    [ProducesResponseType(typeof(PagedResult<OperationalAuditLogDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPaged(
        [FromQuery] Guid? pipelineRunId,
        [FromQuery] string? resourceType,
        [FromQuery] string? action,
        [FromQuery] string? status,
        [FromQuery] string? search,
        [FromQuery] string? severity,
        [FromQuery] int page,
        [FromQuery] int pageSize,
        CancellationToken cancellationToken)
    {
        var result = await _auditService.GetPagedAsync(
            new OperationalAuditLogFilter(pipelineRunId, resourceType, action, status, search, severity),
            page <= 0 ? 1 : page,
            pageSize <= 0 ? 25 : pageSize,
            cancellationToken);

        return Ok(result);
    }
}
