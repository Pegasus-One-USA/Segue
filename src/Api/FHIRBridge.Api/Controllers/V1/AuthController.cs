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
    private readonly ISetupService _setupService;
    private readonly IConfiguration _configuration;

    public AuthController(
        IUserAccessService userAccessService,
        ILocalAuthService localAuthService,
        ISetupService setupService,
        IConfiguration configuration)
    {
        _userAccessService = userAccessService;
        _localAuthService = localAuthService;
        _setupService = setupService;
        _configuration = configuration;
    }

    /// <summary>First-run check — true when the deployment still needs its initial SuperAdmin created.</summary>
    [HttpGet("setup-status")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(SetupStatusDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSetupStatus(CancellationToken cancellationToken)
    {
        var requiresSetup = await _setupService.RequiresSetupAsync(cancellationToken);

        return Ok(new SetupStatusDto(requiresSetup));
    }

    /// <summary>
    /// First-run creation of the sole SuperAdmin (local/password). Anonymous but one-shot: returns 409 once any user
    /// exists. On success returns a signed-in session.
    /// </summary>
    [HttpPost("setup-superadmin")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(LocalLoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SetupSuperAdmin(
        [FromBody] CreateFirstSuperAdminRequest request,
        CancellationToken cancellationToken)
    {
        if (!await _setupService.RequiresSetupAsync(cancellationToken))
        {
            return Conflict(new { error = "setup_already_completed", message = "A user already exists; setup is complete." });
        }

        var response = await _setupService.CreateFirstSuperAdminAsync(request, cancellationToken);

        return Ok(response);
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

    [HttpPost("internal/login")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(LocalLoginResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> LocalLogin(
        [FromBody] LocalLoginRequest request,
        CancellationToken cancellationToken)
    {
        var response = await _localAuthService.LoginAsync(request, cancellationToken);

        return Ok(response);
    }

    [HttpPost("internal/change-password")]
    [ProducesResponseType(typeof(LocalLoginResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> ChangePassword(
        [FromBody] ChangePasswordRequest request,
        CancellationToken cancellationToken)
    {
        var response = await _localAuthService.ChangePasswordAsync(request, cancellationToken);

        return Ok(response);
    }

    [HttpPost("internal/forgot-password")]
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

    [HttpPost("internal/reset-password")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> ResetPassword(
        [FromBody] ResetPasswordRequest request,
        CancellationToken cancellationToken)
    {
        await _localAuthService.ResetPasswordAsync(request, cancellationToken);

        return NoContent();
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(LocalLoginResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Refresh(
        [FromBody] RefreshTokenRequest request,
        CancellationToken cancellationToken)
    {
        var response = await _localAuthService.RefreshTokenAsync(request, cancellationToken);

        return Ok(response);
    }

    [HttpPost("logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        await _localAuthService.LogoutAsync(cancellationToken);

        return NoContent();
    }
}
