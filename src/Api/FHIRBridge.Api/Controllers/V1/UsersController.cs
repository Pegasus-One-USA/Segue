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
    [ProducesResponseType(typeof(IReadOnlyList<UserManagementDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll(CancellationToken cancellationToken)
    {
        var users = await _userManagementService.GetUsersAsync(cancellationToken);

        return Ok(users);
    }

    [HttpPost]
    [ProducesResponseType(typeof(UserManagementDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateLocalUser(
        [FromBody] CreateLocalUserRequest request,
        CancellationToken cancellationToken)
    {
        var user = await _userManagementService.CreateLocalUserAsync(request, cancellationToken);

        return Created($"/api/v1/users/{user.Id}", user);
    }

    [HttpPut("{userId:guid}")]
    [ProducesResponseType(typeof(UserManagementDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateLocalUser(
        Guid userId,
        [FromBody] UpdateLocalUserRequest request,
        CancellationToken cancellationToken)
    {
        var user = await _userManagementService.UpdateLocalUserAsync(userId, request, cancellationToken);

        return Ok(user);
    }
}
