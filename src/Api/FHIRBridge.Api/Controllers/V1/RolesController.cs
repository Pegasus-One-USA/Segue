using FHIRBridge.Api.Security;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using FHIRBridge.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

[ApiController]
[Authorize]
[Route("api/v1/roles")]
public sealed class RolesController : ControllerBase
{
    private readonly IRoleManagementService _roleManagementService;

    public RolesController(IRoleManagementService roleManagementService)
    {
        _roleManagementService = roleManagementService;
    }

    [HttpGet]
    [StandardPermission(PermissionGroupCode.Role, PermissionActionCode.View, description: "View roles and their permissions.")]
    [ProducesResponseType(typeof(IReadOnlyList<RoleDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll(CancellationToken cancellationToken)
    {
        var roles = await _roleManagementService.GetRolesAsync(cancellationToken);

        return Ok(roles);
    }

    [HttpGet("paged")]
    [StandardPermission(PermissionGroupCode.Role, PermissionActionCode.View, description: "View roles and their permissions.")]
    [ProducesResponseType(typeof(PagedResult<RoleDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPaged(
        [FromQuery] string? search,
        [FromQuery] bool? sortDescending,
        [FromQuery] int page,
        [FromQuery] int pageSize,
        CancellationToken cancellationToken)
    {
        var result = await _roleManagementService.GetPagedRolesAsync(
            search, sortDescending, page <= 0 ? 1 : page, pageSize <= 0 ? 10 : pageSize, cancellationToken);
        return Ok(result);
    }

    [HttpGet("{roleId:guid}")]
    [StandardPermission(PermissionGroupCode.Role, PermissionActionCode.View, description: "View roles and their permissions.")]
    [ProducesResponseType(typeof(RoleDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid roleId, CancellationToken cancellationToken)
    {
        var role = await _roleManagementService.GetRoleByIdAsync(roleId, cancellationToken);

        return Ok(role);
    }

    [HttpPost]
    [StandardPermission(PermissionGroupCode.Role, PermissionActionCode.Create, description: "Create a new custom role.")]
    [ProducesResponseType(typeof(RoleDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(
        [FromBody] CreateRoleRequest request,
        CancellationToken cancellationToken)
    {
        var role = await _roleManagementService.CreateRoleAsync(request, cancellationToken);

        return Created($"/api/v1/roles/{role.Id}", role);
    }

    [HttpPut("{roleId:guid}")]
    [StandardPermission(PermissionGroupCode.Role, PermissionActionCode.Edit, description: "Edit an existing role.")]
    [ProducesResponseType(typeof(RoleDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(
        Guid roleId,
        [FromBody] UpdateRoleRequest request,
        CancellationToken cancellationToken)
    {
        var role = await _roleManagementService.UpdateRoleAsync(roleId, request, cancellationToken);

        return Ok(role);
    }

    [HttpDelete("{roleId:guid}")]
    [StandardPermission(PermissionGroupCode.Role, PermissionActionCode.Delete, description: "Delete a custom role.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Delete(Guid roleId, CancellationToken cancellationToken)
    {
        await _roleManagementService.DeleteRoleAsync(roleId, cancellationToken);

        return NoContent();
    }

    [HttpGet("{roleId:guid}/permissions")]
    [StandardPermission(PermissionGroupCode.Role, PermissionActionCode.View, description: "View roles and their permissions.")]
    [ProducesResponseType(typeof(IReadOnlyList<PermissionDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPermissions(Guid roleId, CancellationToken cancellationToken)
    {
        var permissions = await _roleManagementService.GetRolePermissionsAsync(roleId, cancellationToken);

        return Ok(permissions);
    }

    [HttpPost("{roleId:guid}/permissions")]
    [StandardPermission(PermissionGroupCode.Role, PermissionActionCode.Edit, description: "Edit an existing role.")]
    [ProducesResponseType(typeof(RoleDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> AddPermissions(
        Guid roleId,
        [FromBody] AddRolePermissionsRequest request,
        CancellationToken cancellationToken)
    {
        var role = await _roleManagementService.AddRolePermissionsAsync(roleId, request, cancellationToken);

        return Ok(role);
    }

    [HttpDelete("{roleId:guid}/permissions/{permissionId:guid}")]
    [StandardPermission(PermissionGroupCode.Role, PermissionActionCode.Edit, description: "Edit an existing role.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> RemovePermission(
        Guid roleId,
        Guid permissionId,
        CancellationToken cancellationToken)
    {
        await _roleManagementService.RemoveRolePermissionAsync(roleId, permissionId, cancellationToken);

        return NoContent();
    }
}
