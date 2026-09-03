using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Tenancy;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

/// <summary>
/// Tenant CRUD — the real backend for the portal's Tenant Management screens (tenant-list, tenant-dialog,
/// tenant-tab), previously backed only by an in-memory, non-persistent mock (TenantRoleService). Creating,
/// editing, and deleting the isolation boundaries between customers is too sensitive to delegate to the
/// general Admin role — SuperAdmin-only, same reasoning as AllowedCorsOriginsController.
/// </summary>
[ApiController]
[Authorize(Policy = AuthorizationPolicies.SuperAdminOnly)]
[Route("api/v1/tenants")]
public sealed class TenantsController : ControllerBase
{
    private readonly ITenantsService _service;

    public TenantsController(ITenantsService service)
    {
        _service = service;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<TenantDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll(CancellationToken cancellationToken)
    {
        var tenants = await _service.GetAllAsync(cancellationToken);
        return Ok(tenants);
    }

    [HttpGet("paged")]
    [ProducesResponseType(typeof(PagedResult<TenantDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPaged(
        [FromQuery] string? search, [FromQuery] int page, [FromQuery] int pageSize, CancellationToken cancellationToken)
    {
        var result = await _service.GetPagedAsync(search, page <= 0 ? 1 : page, pageSize <= 0 ? 10 : pageSize, cancellationToken);
        return Ok(result);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(TenantDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken)
    {
        var tenant = await _service.GetByIdAsync(id, cancellationToken);
        return Ok(tenant);
    }

    [HttpPost]
    [ProducesResponseType(typeof(TenantDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromBody] CreateTenantRequest request, CancellationToken cancellationToken)
    {
        var tenant = await _service.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = tenant.Id }, tenant);
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(TenantDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(
        Guid id, [FromBody] UpdateTenantRequest request, CancellationToken cancellationToken)
    {
        var tenant = await _service.UpdateAsync(id, request, cancellationToken);
        return Ok(tenant);
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await _service.DeleteAsync(id, cancellationToken);
        return NoContent();
    }
}
