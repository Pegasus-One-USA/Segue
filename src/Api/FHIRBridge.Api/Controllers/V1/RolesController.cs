using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using FHIRBridge.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

[ApiController]
[Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
[Route("api/v1/roles")]
public sealed class RolesController : ControllerBase
{
    private readonly IRoleManagementService _roleManagementService;

    public RolesController(IRoleManagementService roleManagementService)
    {
        _roleManagementService = roleManagementService;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<RoleDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll(CancellationToken cancellationToken)
    {
        var roles = await _roleManagementService.GetRolesAsync(cancellationToken);

        return Ok(roles);
    }

    [HttpGet("permissions")]
    [ProducesResponseType(typeof(IReadOnlyList<PermissionDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPermissions(CancellationToken cancellationToken)
    {
        var permissions = await _roleManagementService.GetPermissionsAsync(cancellationToken);

        return Ok(permissions);
    }

    [HttpPost]
    [ProducesResponseType(typeof(RoleDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(
        [FromBody] CreateRoleRequest request,
        CancellationToken cancellationToken)
    {
        var role = await _roleManagementService.CreateRoleAsync(request, cancellationToken);

        return Created($"/api/v1/roles/{role.Id}", role);
    }

    [HttpPut("{roleId:guid}")]
    [ProducesResponseType(typeof(RoleDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(
        Guid roleId,
        [FromBody] UpdateRoleRequest request,
        CancellationToken cancellationToken)
    {
        var role = await _roleManagementService.UpdateRoleAsync(roleId, request, cancellationToken);

        return Ok(role);
    }

    [HttpDelete("{roleId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid roleId, CancellationToken cancellationToken)
    {
        await _roleManagementService.DeleteRoleAsync(roleId, cancellationToken);

        return NoContent();
    }
}
