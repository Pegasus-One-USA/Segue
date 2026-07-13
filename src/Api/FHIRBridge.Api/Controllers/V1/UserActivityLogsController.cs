using FHIRBridge.Api.Security;
using FHIRBridge.Application.Abstractions.Audit;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

/// <summary>
/// Read-only query API over the append-only, tamper-evident <c>UserActivityAuditLogs</c> trail (who did what,
/// when, from where). Distinct from <see cref="OperationalAuditLogsController"/>, which serves pipeline/system
/// events.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/user-activity-logs")]
public sealed class UserActivityLogsController : ControllerBase
{
    private readonly IUserActivityAuditService _userActivityAuditService;

    public UserActivityLogsController(IUserActivityAuditService userActivityAuditService)
    {
        _userActivityAuditService = userActivityAuditService;
    }

    [HttpGet]
    [StandardPermission(PermissionGroupCode.AuditLogs, PermissionActionCode.Read, description: "View user activity logs.")]
    [ProducesResponseType(typeof(PagedResult<UserActivityAuditLogDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPaged(
        [FromQuery] string? category,
        [FromQuery] string? status,
        [FromQuery] Guid? userId,
        [FromQuery] string? search,
        [FromQuery] int page,
        [FromQuery] int pageSize,
        CancellationToken cancellationToken)
    {
        var result = await _userActivityAuditService.GetPagedAsync(
            new UserActivityLogFilter(category, status, userId, search),
            page <= 0 ? 1 : page,
            pageSize <= 0 ? 25 : pageSize,
            cancellationToken);

        return Ok(result);
    }
}
