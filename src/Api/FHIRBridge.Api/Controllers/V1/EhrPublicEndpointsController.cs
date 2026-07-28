using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FHIRBridge.Api.Controllers.V1;

/// <summary>
/// Anonymous hospital/organization picker listing for third-party apps building a SMART Standalone Launch flow.
/// Merges what used to be two separate, type-locked controllers (ehr-epic-endpoints for Provider Standalone,
/// ehr-mychart-endpoints for Patient Standalone) into a single unfiltered listing — every EhrEndpoint row,
/// regardless of EndpointType. Both Demo_TestApp frontends (Provider and Patient Standalone) call this endpoint.
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
    public async Task<IActionResult> List([FromQuery] string? search, CancellationToken cancellationToken)
    {
        var endpoints = await _service.GetPublicEndpointsAsync(search, cancellationToken);
        return Ok(endpoints);
    }
}
