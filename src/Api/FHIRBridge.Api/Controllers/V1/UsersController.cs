using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using FHIRBridge.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

[ApiController]
[Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
[Route("api/v1/users")]
public sealed class UsersController : ControllerBase
{
    private readonly IUserManagementService _userManagementService;

    public UsersController(IUserManagementService userManagementService)
    {
        _userManagementService = userManagementService;
    }

    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
    [ProducesResponseType(typeof(IReadOnlyList<UserManagementDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll(CancellationToken cancellationToken)
    {
        var users = await _userManagementService.GetUsersAsync(cancellationToken);

        return Ok(users);
    }

    [HttpGet("{userId:guid}")]
    [Authorize(Policy = "HasPermission:user.view")]
    [ProducesResponseType(typeof(UserDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid userId, CancellationToken cancellationToken)
    {
        var user = await _userManagementService.GetUserByIdAsync(userId, cancellationToken);

        return Ok(user);
    }

    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
    [ProducesResponseType(typeof(UserManagementDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateLocalUser(
        [FromBody] CreateLocalUserRequest request,
        CancellationToken cancellationToken)
    {
        var user = await _userManagementService.CreateLocalUserAsync(request, cancellationToken);

        return Created($"/api/v1/users/{user.Id}", user);
    }

    [HttpPut("{userId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
    [ProducesResponseType(typeof(UserManagementDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateLocalUser(
        Guid userId,
        [FromBody] UpdateLocalUserRequest request,
        CancellationToken cancellationToken)
    {
        var user = await _userManagementService.UpdateLocalUserAsync(userId, request, cancellationToken);

        return Ok(user);
    }

    [HttpPost("invite")]
    [Authorize(Policy = "HasPermission:user.invite")]
    [ProducesResponseType(typeof(UserDetailDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> InviteUser(
        [FromBody] InviteUserRequest request,
        CancellationToken cancellationToken)
    {
        var user = await _userManagementService.InviteUserAsync(request, cancellationToken);

        return Created($"/api/v1/users/{user.Id}", user);
    }

    [HttpPost("accept-invite")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(UserDetailDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> AcceptInvite(
        [FromBody] AcceptInviteRequest request,
        CancellationToken cancellationToken)
    {
        var user = await _userManagementService.AcceptInviteAsync(request, cancellationToken);

        return Ok(user);
    }

    [HttpPatch("{userId:guid}/status")]
    [Authorize(Policy = "HasPermission:user.deactivate")]
    [ProducesResponseType(typeof(UserDetailDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateUserStatus(
        Guid userId,
        [FromBody] UpdateUserStatusRequest request,
        CancellationToken cancellationToken)
    {
        var user = await _userManagementService.UpdateUserStatusAsync(userId, request, cancellationToken);

        return Ok(user);
    }

    [HttpDelete("{userId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> DeleteUser(Guid userId, CancellationToken cancellationToken)
    {
        await _userManagementService.DeleteUserAsync(userId, cancellationToken);

        return NoContent();
    }

    [HttpGet("{userId:guid}/roles")]
    [Authorize(Policy = "HasPermission:user.view")]
    [ProducesResponseType(typeof(IReadOnlyList<RoleDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetUserRoles(Guid userId, CancellationToken cancellationToken)
    {
        var roles = await _userManagementService.GetUserRolesAsync(userId, cancellationToken);

        return Ok(roles);
    }

    [HttpPost("{userId:guid}/roles")]
    [Authorize(Policy = "HasPermission:role.assign")]
    [ProducesResponseType(typeof(UserDetailDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> AssignUserRole(
        Guid userId,
        [FromBody] AssignUserRoleRequest request,
        CancellationToken cancellationToken)
    {
        var user = await _userManagementService.AssignUserRoleAsync(userId, request, cancellationToken);

        return Ok(user);
    }

    [HttpDelete("{userId:guid}/roles/{roleId:guid}")]
    [Authorize(Policy = "HasPermission:role.assign")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> RemoveUserRole(
        Guid userId,
        Guid roleId,
        CancellationToken cancellationToken)
    {
        await _userManagementService.RemoveUserRoleAsync(userId, roleId, cancellationToken);

        return NoContent();
    }
}
