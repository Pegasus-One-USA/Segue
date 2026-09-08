using FHIRBridge.Api.Security;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Enums;
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
[Authorize]
[Route("api/v1/ehr-endpoints")]
public sealed class EhrEndpointsController : ControllerBase
{
    private readonly IEhrEndpointService _service;

    public EhrEndpointsController(IEhrEndpointService service)
    {
        _service = service;
    }

    [HttpGet]
    [StandardPermission(PermissionGroupCode.EhrEndpoints, PermissionActionCode.View, description: "View the EHR endpoint directory.")]
    [ProducesResponseType(typeof(IReadOnlyList<EhrEndpointDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var endpoints = await _service.GetAllAsync(cancellationToken);
        return Ok(endpoints);
    }

    [HttpGet("paged")]
    [StandardPermission(PermissionGroupCode.EhrEndpoints, PermissionActionCode.View, description: "View the EHR endpoint directory.")]
    [ProducesResponseType(typeof(PagedResult<EhrEndpointDto>), StatusCodes.Status200OK)]
    /// <param name="vendor">The screen's "Source" dropdown — a <see cref="SourceSystemType"/> member name.</param>
    /// <param name="isActive">The screen's "Status" dropdown; see <see cref="EhrEndpointFilter.IsActive"/>.</param>
    public async Task<IActionResult> ListPaged(
        [FromQuery] string? search,
        [FromQuery] SourceSystemType? vendor,
        [FromQuery] bool? isActive,
        [FromQuery] bool? sortDescending,
        [FromQuery] int page,
        [FromQuery] int pageSize,
        CancellationToken cancellationToken)
    {
        var filter = new EhrEndpointFilter(search, vendor, isActive);
        var result = await _service.GetPagedAsync(
            filter, sortDescending, page <= 0 ? 1 : page, pageSize <= 0 ? 10 : pageSize, cancellationToken);
        return Ok(result);
    }

    [HttpGet("{id:guid}")]
    [StandardPermission(PermissionGroupCode.EhrEndpoints, PermissionActionCode.View, description: "View the EHR endpoint directory.")]
    [ProducesResponseType(typeof(EhrEndpointDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken)
    {
        var endpoint = await _service.GetByIdAsync(id, cancellationToken);
        return endpoint is null ? NotFound() : Ok(endpoint);
    }

    [HttpPost]
    [StandardPermission(PermissionGroupCode.EhrEndpoints, PermissionActionCode.Create, description: "Add an EHR endpoint.")]
    [ProducesResponseType(typeof(EhrEndpointDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromBody] CreateEhrEndpointRequest request, CancellationToken cancellationToken)
    {
        var endpoint = await _service.AddAsync(request, cancellationToken);
        return Created($"/api/v1/ehr-endpoints/{endpoint.Id}", endpoint);
    }

    [HttpPut("{id:guid}")]
    [StandardPermission(PermissionGroupCode.EhrEndpoints, PermissionActionCode.Edit, description: "Edit an EHR endpoint.")]
    [ProducesResponseType(typeof(EhrEndpointDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(Guid id, [FromBody] CreateEhrEndpointRequest request, CancellationToken cancellationToken)
    {
        var endpoint = await _service.UpdateAsync(id, request, cancellationToken);
        return Ok(endpoint);
    }

    [HttpDelete("{id:guid}")]
    [StandardPermission(PermissionGroupCode.EhrEndpoints, PermissionActionCode.Delete, description: "Delete an EHR endpoint.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await _service.DeleteAsync(id, cancellationToken);
        return NoContent();
    }
}
