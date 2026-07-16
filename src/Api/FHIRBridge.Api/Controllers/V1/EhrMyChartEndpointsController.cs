using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FHIRBridge.Api.Controllers.V1;

/// <summary>
/// Anonymous, read-only listing of EhrEndpoint rows tagged EndpointType.MyChart (a specific customer/hospital's own
/// branded production instance — never the vendor's shared test sandbox, which stays behind
/// EhrEpicEndpointsController). For a third-party app building a hospital picker ahead of an unauthenticated SMART
/// Patient Standalone Launch, which by definition has no FHIRBridge session to present. Deliberately its own
/// controller/service (not a change to EhrEpicEndpointsController or IEhrEndpointService) so the Provider Standalone
/// flow's existing Epic-only surface never has to change to support this.
/// </summary>
[ApiController]
[AllowAnonymous]
[EnableRateLimiting("oauth")]
[Route("api/v1/ehr-mychart-endpoints")]
public sealed class EhrMyChartEndpointsController : ControllerBase
{
    private readonly IPatientStandaloneEhrEndpointService _service;

    public EhrMyChartEndpointsController(IPatientStandaloneEhrEndpointService service)
    {
        _service = service;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<PublicEhrEpicEndpointDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] string? search, CancellationToken cancellationToken)
    {
        var endpoints = await _service.GetPublicMyChartEndpointsAsync(search, cancellationToken);
        return Ok(endpoints);
    }
}
