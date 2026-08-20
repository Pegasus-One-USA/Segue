using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using FHIRBridge.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

[ApiController]
[Route("api/v1/permissions")]
public sealed class PermissionsController : ControllerBase
{
    private readonly IRoleManagementService _roleManagementService;

    public PermissionsController(IRoleManagementService roleManagementService)
    {
        _roleManagementService = roleManagementService;
    }

    // Admin-only, unchanged — the class-level [Authorize(UnifiedAdmin)] this used to inherit is now
    // declared here explicitly instead, since GetNodeCatalog below must NOT require it.
    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
    [ProducesResponseType(typeof(IReadOnlyList<PermissionDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll(CancellationToken cancellationToken)
    {
        var permissions = await _roleManagementService.GetPermissionsAsync(cancellationToken);

        return Ok(permissions);
    }

    // Admin-only, unchanged — see GetAll's comment above.
    [HttpGet("catalog")]
    [Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
    [ProducesResponseType(typeof(IReadOnlyList<PermissionCatalogCategoryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCatalog(CancellationToken cancellationToken)
    {
        var catalog = await _roleManagementService.GetPermissionCatalogAsync(cancellationToken);

        return Ok(catalog);
    }

    // Additive — GetCatalog above is untouched, so any existing consumer of /permissions/catalog sees
    // no contract change. This is the canonical Node Catalog: the single source of truth the Workflow
    // Builder Node Library and Role Permissions' Workflow Nodes section both read from instead of each
    // maintaining their own node list. See NodeCatalogBuilder/NodeCatalogMetadata.
    //
    // Deliberately just [Authorize] (any authenticated user), NOT UnifiedAdmin — this describes node
    // AVAILABILITY (rollout + which permission, if any, governs each node), not a per-node grant. Any
    // authenticated user needs to reach it to render their own Node Library, regardless of role; the
    // actual view/create/edit/delete/execute enforcement stays exactly where it already was (the real
    // endpoints in WorkflowEndpoints.cs / ConfigurationsController.cs / etc.), unaffected by this.
    [HttpGet("node-catalog")]
    [Authorize]
    [ProducesResponseType(typeof(IReadOnlyList<NodeCatalogEntryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetNodeCatalog(CancellationToken cancellationToken)
    {
        var catalog = await _roleManagementService.GetNodeCatalogAsync(cancellationToken);

        return Ok(catalog);
    }
}
