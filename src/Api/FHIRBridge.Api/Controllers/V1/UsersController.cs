using FHIRBridge.Api.Security;
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
    [StandardPermission(PermissionGroupCode.User, PermissionActionCode.View, description: "View the list of users.")]
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
    [StandardPermission(PermissionGroupCode.User, PermissionActionCode.Invite, description: "Invite a new user to the organization.")]
    [ProducesResponseType(typeof(UserDetailDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> InviteUser(
        [FromBody] InviteUserRequest request,
        CancellationToken cancellationToken)
    {
        var user = await _userManagementService.InviteUserAsync(request, cancellationToken);

        return Created($"/api/v1/users/{user.Id}", user);
    }

    [HttpPost("{userId:guid}/resend-invite")]
    [StandardPermission(PermissionGroupCode.User, PermissionActionCode.Invite, description: "Invite a new user to the organization.")]
    [ProducesResponseType(typeof(UserDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ResendInvite(Guid userId, CancellationToken cancellationToken)
    {
        var user = await _userManagementService.ResendInviteAsync(userId, cancellationToken);

        return Ok(user);
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

    [HttpPost("accept-invite-sso")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(LocalLoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> AcceptInviteSso(
        [FromBody] AcceptInviteSsoRequest request,
        CancellationToken cancellationToken)
    {
        var response = await _userManagementService.AcceptInviteViaSsoAsync(request, cancellationToken);

        return Ok(response);
    }

    [HttpPatch("{userId:guid}/status")]
    [StandardPermission(PermissionGroupCode.User, PermissionActionCode.Deactivate, description: "Deactivate a user account.")]
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
    [StandardPermission(PermissionGroupCode.User, PermissionActionCode.View, description: "View the list of users.")]
    [ProducesResponseType(typeof(IReadOnlyList<RoleDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetUserRoles(Guid userId, CancellationToken cancellationToken)
    {
        var roles = await _userManagementService.GetUserRolesAsync(userId, cancellationToken);

        return Ok(roles);
    }

    [HttpPost("{userId:guid}/roles")]
    [StandardPermission(PermissionGroupCode.Role, PermissionActionCode.Assign, description: "Assign or remove roles from users.")]
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
    [StandardPermission(PermissionGroupCode.Role, PermissionActionCode.Assign, description: "Assign or remove roles from users.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> RemoveUserRole(
        Guid userId,
        Guid roleId,
        CancellationToken cancellationToken)
    {
        await _userManagementService.RemoveUserRoleAsync(userId, roleId, cancellationToken);

        return NoContent();
    }

    [HttpGet("{userId:guid}/permission-allocations")]
    [StandardPermission(PermissionGroupCode.User, PermissionActionCode.View, description: "View the list of users.")]
    [ProducesResponseType(typeof(IReadOnlyList<PermissionAllocationDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetUserPermissionAllocations(Guid userId, CancellationToken cancellationToken)
    {
        var allocations = await _userManagementService.GetUserPermissionAllocationsAsync(userId, cancellationToken);

        return Ok(allocations);
    }

    [HttpPut("{userId:guid}/permission-allocations/{permissionId:guid}")]
    [StandardPermission(PermissionGroupCode.User, PermissionActionCode.Edit, description: "Update a user's profile information.")]
    [ProducesResponseType(typeof(UserDetailDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> SetUserPermissionAllocation(
        Guid userId,
        Guid permissionId,
        [FromBody] UpsertUserPermissionAllocationRequest request,
        CancellationToken cancellationToken)
    {
        var user = await _userManagementService.SetUserPermissionAllocationAsync(userId, permissionId, request, cancellationToken);

        return Ok(user);
    }

    [HttpDelete("{userId:guid}/permission-allocations/{permissionId:guid}")]
    [StandardPermission(PermissionGroupCode.User, PermissionActionCode.Edit, description: "Update a user's profile information.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> RemoveUserPermissionAllocation(
        Guid userId,
        Guid permissionId,
        CancellationToken cancellationToken)
    {
        await _userManagementService.RemoveUserPermissionAllocationAsync(userId, permissionId, cancellationToken);

        return NoContent();
    }





    [HttpDelete("{userId:guid}/permission-allocations1/{permissionId:guid}")]
    [StandardPermission(PermissionGroupCode.Epic, PermissionActionCode.Assign, description: "Update a user's profile information.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> RemoveUserPermissionAllocation1(
        Guid userId,
        Guid permissionId,
        CancellationToken cancellationToken)
    {
        await _userManagementService.RemoveUserPermissionAllocationAsync(userId, permissionId, cancellationToken);

        return NoContent();
    }


    [HttpDelete("{userId:guid}/permission-allocations2/{permissionId:guid}")]
    [StandardPermission(PermissionGroupCode.Epic, PermissionActionCode.Read, description: "Update a user's profile information.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> RemoveUserPermissionAllocation2(
        Guid userId,
        Guid permissionId,
        CancellationToken cancellationToken)
    {
        await _userManagementService.RemoveUserPermissionAllocationAsync(userId, permissionId, cancellationToken);

        return NoContent();
    }


    [HttpDelete("{userId:guid}/permission-allocations3/{permissionId:guid}")]
    [StandardPermission(PermissionGroupCode.Athena, PermissionActionCode.Assign, description: "Update a user's profile information.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> RemoveUserPermissionAllocation3(
        Guid userId,
        Guid permissionId,
        CancellationToken cancellationToken)
    {
        await _userManagementService.RemoveUserPermissionAllocationAsync(userId, permissionId, cancellationToken);

        return NoContent();
    }



    [HttpDelete("{userId:guid}/permission-allocations31/{permissionId:guid}")]
    [StandardPermission(PermissionGroupCode.Athena, PermissionActionCode.Assign, description: "Update a user's profile information 1.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> RemoveUserPermissionAllocation31(
        Guid userId,
        Guid permissionId,
        CancellationToken cancellationToken)
    {
        await _userManagementService.RemoveUserPermissionAllocationAsync(userId, permissionId, cancellationToken);

        return NoContent();
    }



    [HttpDelete("{userId:guid}/permission-allocations4/{permissionId:guid}")]
    [StandardPermission(PermissionGroupCode.Athena, PermissionActionCode.Read, description: "Update a user's profile information.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> RemoveUserPermissionAllocation4(
        Guid userId,
        Guid permissionId,
        CancellationToken cancellationToken)
    {
        await _userManagementService.RemoveUserPermissionAllocationAsync(userId, permissionId, cancellationToken);

        return NoContent();
    }


    [HttpDelete("{userId:guid}/permission-allocations41/{permissionId:guid}")]
    [StandardPermission(PermissionGroupCode.Athena, PermissionActionCode.Read, description: "Update a user's profile information.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> RemoveUserPermissionAllocation41(
        Guid userId,
        Guid permissionId,
        CancellationToken cancellationToken)
    {
        await _userManagementService.RemoveUserPermissionAllocationAsync(userId, permissionId, cancellationToken);

        return NoContent();
    }


    [HttpDelete("{userId:guid}/permission-allocations42/{permissionId:guid}")]
    [StandardPermission(PermissionGroupCode.Athena, PermissionActionCode.Read, description: "Update a user's profile information.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> RemoveUserPermissionAllocation42(
        Guid userId,
        Guid permissionId,
        CancellationToken cancellationToken)
    {
        await _userManagementService.RemoveUserPermissionAllocationAsync(userId, permissionId, cancellationToken);

        return NoContent();
    }
}
