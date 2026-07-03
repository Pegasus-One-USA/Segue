using FHIRBridge.Application.Abstractions.Audit;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

[ApiController]
[Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
[Route("api/v1/audit-logs")]
public sealed class OperationalAuditLogsController : ControllerBase
{
    private readonly IOperationalAuditService _auditService;

    public OperationalAuditLogsController(IOperationalAuditService auditService)
    {
        _auditService = auditService;
    }

    [HttpGet]
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
}
