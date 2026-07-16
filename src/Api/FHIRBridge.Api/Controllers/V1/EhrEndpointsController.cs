using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using FHIRBridge.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

/// <summary>
/// CRUD over the vendor EHR endpoint directory (e.g. Epic's published list of client-organization FHIR base URLs).
/// Rows are normally imported by IEhrEndpointDirectorySeeder implementations, but admins can also manage them by
/// hand here — e.g. to pick a known endpoint by id when configuring a SourceConnection, or to add an org for a
/// vendor with no directory seeder yet.
/// </summary>
[ApiController]
[Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
[Route("api/v1/ehr-endpoints")]
public sealed class EhrEndpointsController : ControllerBase
{
    private readonly IEhrEndpointService _service;

    public EhrEndpointsController(IEhrEndpointService service)
    {
        _service = service;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<EhrEndpointDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var endpoints = await _service.GetAllAsync(cancellationToken);
        return Ok(endpoints);
    }

    /// <summary>
    /// Unauthenticated, lean directory listing (id + name only, active endpoints only) for public-facing pickers —
    /// e.g. a patient-facing demo app's "choose your hospital" screen — that have no FHIRBridge admin session.
    /// </summary>
    [HttpGet("public")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(IReadOnlyList<PublicEhrEndpointDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListPublic(CancellationToken cancellationToken)
    {
        // GetAllAsync already returns rows ordered by Name, so no re-sort is needed here.
        var endpoints = await _service.GetAllAsync(cancellationToken);
        var active = endpoints
            .Where(e => string.Equals(e.Status, "active", StringComparison.OrdinalIgnoreCase))
            .Select(e => new PublicEhrEndpointDto(e.Id, e.Name))
            .ToArray();

        return Ok(active);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(EhrEndpointDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken)
    {
        var endpoint = await _service.GetByIdAsync(id, cancellationToken);
        return endpoint is null ? NotFound() : Ok(endpoint);
    }

    [HttpPost]
    [ProducesResponseType(typeof(EhrEndpointDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromBody] CreateEhrEndpointRequest request, CancellationToken cancellationToken)
    {
        var endpoint = await _service.AddAsync(request, cancellationToken);
        return Created($"/api/v1/ehr-endpoints/{endpoint.Id}", endpoint);
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(EhrEndpointDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(Guid id, [FromBody] CreateEhrEndpointRequest request, CancellationToken cancellationToken)
    {
        var endpoint = await _service.UpdateAsync(id, request, cancellationToken);
        return Ok(endpoint);
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await _service.DeleteAsync(id, cancellationToken);
        return NoContent();
    }
}
