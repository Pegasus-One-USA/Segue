using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

[ApiController]
[Authorize]
[Route("api/v1/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly IUserAccessService _userAccessService;
    private readonly ILocalAuthService _localAuthService;
    private readonly IConfiguration _configuration;

    public AuthController(
        IUserAccessService userAccessService,
        ILocalAuthService localAuthService,
        IConfiguration configuration)
    {
        _userAccessService = userAccessService;
        _localAuthService = localAuthService;
        _configuration = configuration;
    }

    [HttpGet("me")]
    [ProducesResponseType(typeof(UserProfileDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCurrentUser(CancellationToken cancellationToken)
    {
        var profile = await _userAccessService.GetCurrentUserProfileAsync(cancellationToken);

        return Ok(profile);
    }

    [HttpPost("login")]
    [ProducesResponseType(typeof(UserProfileDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> RecordLogin(CancellationToken cancellationToken)
    {
        var profile = await _userAccessService.RecordLoginAsync(cancellationToken);

        return Ok(profile);
    }

    [HttpPost("local/login")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(LocalLoginResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> LocalLogin(
        [FromBody] LocalLoginRequest request,
        CancellationToken cancellationToken)
    {
        var response = await _localAuthService.LoginAsync(request, cancellationToken);

        return Ok(response);
    }

    [HttpPost("local/change-password")]
    [ProducesResponseType(typeof(LocalLoginResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> ChangePassword(
        [FromBody] ChangePasswordRequest request,
        CancellationToken cancellationToken)
    {
        var response = await _localAuthService.ChangePasswordAsync(request, cancellationToken);

        return Ok(response);
    }

    [HttpPost("local/forgot-password")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ForgotPasswordResponse), StatusCodes.Status202Accepted)]
    public async Task<IActionResult> ForgotPassword(
        [FromBody] ForgotPasswordRequest request,
        CancellationToken cancellationToken)
    {
        var response = await _localAuthService.ForgotPasswordAsync(request, cancellationToken);
        if (!_configuration.GetValue<bool>("LocalAuth:ExposeResetTokens"))
        {
            response = response with { ResetToken = null, ExpiresOnUtc = null };
        }

        return Accepted(response);
    }

    [HttpPost("local/reset-password")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> ResetPassword(
        [FromBody] ResetPasswordRequest request,
        CancellationToken cancellationToken)
    {
        await _localAuthService.ResetPasswordAsync(request, cancellationToken);

        return NoContent();
    }
}
