using FHIRBridge.Application.Abstractions.Licensing;
using FHIRBridge.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

/// <summary>
/// This install's own outbound license request — see <see cref="ILicenseRequestService"/>'s remarks for the
/// full one-request-per-install lifecycle. Same UnifiedAdmin gate as <see cref="LicenseController"/>.
/// </summary>
[ApiController]
[Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
[Route("api/v1/license-request")]
public sealed class LicenseRequestController : ControllerBase
{
    private readonly ILicenseRequestService _service;

    public LicenseRequestController(ILicenseRequestService service)
    {
        _service = service;
    }

    [HttpGet]
    [ProducesResponseType(typeof(LicenseRequestStatusResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken) =>
        Ok(await _service.GetAsync(cancellationToken));

    [HttpPost]
    [ProducesResponseType(typeof(LicenseRequestStatusResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create(
        [FromBody] CreateLicenseRequestRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _service.CreateAndSubmitAsync(
                new LicenseRequestInput(
                    request.ClientName, request.Email, request.CompanyName, request.Address, request.PhoneNumber),
                HttpContext.Request.Host.Value,
                cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = "invalid_request", error_description = ex.Message });
        }
    }

    [HttpPost("resubmit")]
    [ProducesResponseType(typeof(LicenseRequestStatusResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Resubmit(CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _service.ResubmitAsync(cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = "invalid_request", error_description = ex.Message });
        }
    }
}

/// <summary>Body of <c>POST /api/v1/license-request</c>.</summary>
public sealed record CreateLicenseRequestRequest(
    string ClientName, string Email, string? CompanyName, string? Address, string PhoneNumber);
