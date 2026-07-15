using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FHIRBridge.Api.Controllers.V1;

/// <summary>
/// Anonymous, read-only listing of EhrEndpoint rows tagged EndpointType.Epic (the vendor's own shared test
/// sandbox, e.g. https://fhir.epic.com/interconnect-fhir-oauth/api/FHIR/R4 — never a specific customer's MyChart
/// production instance, which stays behind the admin-only EhrEndpointsController). For a third-party app building
/// an organization/hospital picker ahead of an unauthenticated SMART Standalone Launch, which by definition has no
/// FHIRBridge session to present. Deliberately its own controller (not an [AllowAnonymous] action bolted onto the
/// admin-gated one) so the narrower security posture and response shape stay obvious at a glance.
/// </summary>
[ApiController]
[AllowAnonymous]
[EnableRateLimiting("oauth")]
[Route("api/v1/ehr-epic-endpoints")]
public sealed class EhrEpicEndpointsController : ControllerBase
{
    private readonly IEhrEndpointService _service;

    public EhrEpicEndpointsController(IEhrEndpointService service)
    {
        _service = service;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<PublicEhrEpicEndpointDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] string? search, CancellationToken cancellationToken)
    {
        var endpoints = await _service.GetPublicEpicEndpointsAsync(search, cancellationToken);
        return Ok(endpoints);
    }
}
