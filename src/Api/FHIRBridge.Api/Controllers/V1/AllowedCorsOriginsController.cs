using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using FHIRBridge.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

/// <summary>
/// SuperAdmin-only management of the global CORS allowed-origins list — too sensitive to delegate to
/// the general Admin role, since it widens which browser origins can call the API with credentials.
/// </summary>
[ApiController]
[Authorize(Policy = AuthorizationPolicies.SuperAdminOnly)]
[Route("api/v1/system/allowed-origins")]
public sealed class AllowedCorsOriginsController : ControllerBase
{
    private readonly IAllowedCorsOriginsService _service;

    public AllowedCorsOriginsController(IAllowedCorsOriginsService service)
    {
        _service = service;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<AllowedCorsOriginDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll(CancellationToken cancellationToken)
    {
        var origins = await _service.GetAllAsync(cancellationToken);

        return Ok(origins);
    }

    /// <summary>Server-side paged/searchable list backing the Allowed Origins screen. The CORS policy
    /// itself still reads the unpaged list — paging is presentation only and never narrows what the API
    /// actually allows.</summary>
    [HttpGet("paged")]
    [ProducesResponseType(typeof(PagedResult<AllowedCorsOriginDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPaged(
        [FromQuery] string? search,
        [FromQuery] int page,
        [FromQuery] int pageSize,
        CancellationToken cancellationToken)
    {
        // pageSize is clamped at both ends: a caller passing pageSize=1000000 would otherwise materialize the
        // whole table in one request, which is the denial-of-service shape of an unbounded page parameter.
        const int defaultPageSize = 10;
        const int maxPageSize = 200;

        var effectivePageSize = pageSize <= 0
            ? defaultPageSize
            : Math.Min(pageSize, maxPageSize);

        var result = await _service.GetPagedAsync(
            search, page <= 0 ? 1 : page, effectivePageSize, cancellationToken);

        return Ok(result);
    }

    [HttpPost]
    [ProducesResponseType(typeof(AllowedCorsOriginDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> Add(
        [FromBody] CreateAllowedCorsOriginRequest request,
        CancellationToken cancellationToken)
    {
        var origin = await _service.AddAsync(request, cancellationToken);

        return CreatedAtAction(nameof(GetAll), origin);
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(AllowedCorsOriginDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(
        Guid id,
        [FromBody] UpdateAllowedCorsOriginRequest request,
        CancellationToken cancellationToken)
    {
        var origin = await _service.UpdateAsync(id, request, cancellationToken);

        return Ok(origin);
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await _service.DeleteAsync(id, cancellationToken);

        return NoContent();
    }

    /// <summary>
    /// Manually forces every running replica to pick up the current allowed-origins rows immediately —
    /// an explicit "reload now" action, for when an operator doesn't want to wait for the cache's own
    /// staleness bound. Add/Update/Delete above already do this automatically on save.
    /// </summary>
    [HttpPost("reload")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult Reload()
    {
        _service.Reload();

        return NoContent();
    }
}
