using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using FHIRBridge.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

[ApiController]
[Authorize]
[Route("api/v1/permissions")]
public sealed class PermissionsController : ControllerBase
{
    private readonly IRoleManagementService _roleManagementService;

    public PermissionsController(IRoleManagementService roleManagementService)
    {
        _roleManagementService = roleManagementService;
    }

    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
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
    //
    // Gated by PermissionCatalogAccess (UnifiedAdmin OR role.view — see
    // PermissionCatalogAccessAuthorizationHandler), not GetAll's own straight UnifiedAdmin: the Role
    // Permissions screen (user-management.routes.ts) is reachable with role.view alone and needs this
    // catalog to render its read-only grid — without it the whole request 403s and the page renders
    // "Role not found" instead of the promised read-only view.
    [HttpGet("catalog")]
    [Authorize(Policy = AuthorizationPolicies.PermissionCatalogAccess)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType(typeof(IReadOnlyList<PermissionCatalogCategoryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCatalog(CancellationToken cancellationToken)
    {
        var catalog = await _roleManagementService.GetPermissionCatalogAsync(cancellationToken);

        return Ok(catalog);
    }
}
