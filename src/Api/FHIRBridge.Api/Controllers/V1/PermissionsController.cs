using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using FHIRBridge.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

[ApiController]
[Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
[Route("api/v1/permissions")]
public sealed class PermissionsController : ControllerBase
{
    private readonly IRoleManagementService _roleManagementService;

    public PermissionsController(IRoleManagementService roleManagementService)
    {
        _roleManagementService = roleManagementService;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<PermissionDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll(CancellationToken cancellationToken)
    {
        var permissions = await _roleManagementService.GetPermissionsAsync(cancellationToken);

        return Ok(permissions);
    }

    // The catalog reflects live RBAC state (which groups currently have any active permission) — it must
    // never be served stale out of a browser/proxy cache, or a permission-sync change (e.g. deactivating a
    // group whose feature was removed) would keep showing the old list until a hard refresh. GetAll above
    // doesn't need this: nothing currently caches it, and it's a flatter, less state-sensitive read.
    [HttpGet("catalog")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType(typeof(IReadOnlyList<PermissionCatalogCategoryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCatalog(CancellationToken cancellationToken)
    {
        var catalog = await _roleManagementService.GetPermissionCatalogAsync(cancellationToken);

        return Ok(catalog);
    }
}
