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
}
