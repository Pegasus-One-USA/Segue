using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FHIRBridge.Api.Controllers.V1;

/// <summary>
/// Anonymous hospital/organization picker listing for third-party apps building a SMART Standalone Launch flow.
/// One shared controller for both audiences (Provider Standalone / Patient Standalone), but each caller must pass
/// its own <see cref="EhrEndpointType"/> so the two flows never see each other's rows — Provider Standalone
/// (Demo_TestApp's launch-standalone-provider.ts) passes Epic; Patient Standalone
/// (patient-standalone-launch.service.ts) passes MyChart.
/// </summary>
[ApiController]
[AllowAnonymous]
[EnableRateLimiting("oauth")]
[Route("api/v1/ehr-public-endpoints")]
public sealed class EhrPublicEndpointsController : ControllerBase
{
    private readonly IEhrEndpointService _service;

    public EhrPublicEndpointsController(IEhrEndpointService service)
    {
        _service = service;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<PublicEhrEndpointDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] EhrEndpointType endpointType, [FromQuery] string? search, CancellationToken cancellationToken)
    {
        var endpoints = await _service.GetPublicEndpointsAsync(endpointType, search, cancellationToken);
        return Ok(endpoints);
    }
}
