using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FHIRBridge.Api.Controllers.V1;

/// <summary>
/// TOTP multi-factor enrollment and management for the signed-in user. Login-time enforcement of the
/// second factor happens in the local login flow (see <see cref="AuthController"/>).
/// </summary>
[ApiController]
[Authorize]
[EnableRateLimiting("auth")]
[Route("api/v1/auth/mfa")]
public sealed class MfaController : ControllerBase
{
    private readonly IMfaService _mfaService;

    public MfaController(IMfaService mfaService)
    {
        _mfaService = mfaService;
    }

    [HttpGet("status")]
    [ProducesResponseType(typeof(MfaStatusResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStatus(CancellationToken cancellationToken)
        => Ok(await _mfaService.GetStatusAsync(cancellationToken));

    /// <summary>Begins enrollment: stages a new secret and returns it plus an otpauth URI for QR display.</summary>
    [HttpPost("enroll")]
    [ProducesResponseType(typeof(MfaEnrollmentResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Enroll(CancellationToken cancellationToken)
        => Ok(await _mfaService.BeginEnrollmentAsync(cancellationToken));

    /// <summary>Confirms enrollment with a code from the authenticator; returns one-time backup codes.</summary>
    [HttpPost("verify")]
    [ProducesResponseType(typeof(MfaEnrollmentConfirmedResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Verify(
        [FromBody] MfaCodeRequest request,
        CancellationToken cancellationToken)
        => Ok(await _mfaService.ConfirmEnrollmentAsync(request, cancellationToken));

    /// <summary>Disables MFA after validating a current TOTP or backup code.</summary>
    [HttpPost("disable")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Disable(
        [FromBody] MfaCodeRequest request,
        CancellationToken cancellationToken)
    {
        await _mfaService.DisableAsync(request, cancellationToken);
        return NoContent();
    }
}
