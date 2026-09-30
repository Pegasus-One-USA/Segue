using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using FHIRBridge.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

/// <summary>
/// Admin management of Client ID/Secret credentials for the OAuth 2.0 Client Credentials Grant a third-party
/// application uses to trigger workflow execution server-to-server (see <c>ClientCredentialsTokenController</c>
/// for the token endpoint these credentials are redeemed against, and <c>WorkflowEndpoints</c>'s <c>/run</c>
/// handler for where the resulting token is honored). Tenant-wide, not scoped to any single workflow.
/// </summary>
[ApiController]
[Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
[Route("api/v1/api-clients")]
public sealed class ApiClientsController : ControllerBase
{
    private readonly IApiClientService _service;

    public ApiClientsController(IApiClientService service)
    {
        _service = service;
    }

    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<ApiClientDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPaged(
        [FromQuery] string? search, [FromQuery] int page, [FromQuery] int pageSize, CancellationToken cancellationToken)
    {
        const int defaultPageSize = 10;
        const int maxPageSize = 200;

        var effectivePageSize = pageSize <= 0 ? defaultPageSize : Math.Min(pageSize, maxPageSize);

        var result = await _service.GetPagedAsync(search, page <= 0 ? 1 : page, effectivePageSize, cancellationToken);

        return Ok(result);
    }

    /// <summary>Creates a client. The plaintext secret is present in the response exactly once — it is never
    /// stored, and cannot be recovered later; only <see cref="RegenerateSecret"/> can produce a new one.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(ApiClientCredentialDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromBody] CreateApiClientRequest request, CancellationToken cancellationToken)
    {
        var result = await _service.CreateAsync(request, cancellationToken);

        return CreatedAtAction(nameof(GetPaged), null, result);
    }

    [HttpPost("{id:guid}/regenerate-secret")]
    [ProducesResponseType(typeof(ApiClientCredentialDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RegenerateSecret(Guid id, CancellationToken cancellationToken)
    {
        var result = await _service.RegenerateSecretAsync(id, cancellationToken);

        return Ok(result);
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(ApiClientDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateApiClientRequest request, CancellationToken cancellationToken)
    {
        var client = await _service.UpdateAsync(id, request, cancellationToken);

        return Ok(client);
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await _service.DeleteAsync(id, cancellationToken);

        return NoContent();
    }

    /// <summary>Registers an allowed Return URL — required before the browser-redirect external-trigger flow
    /// (<c>POST /api/v1/workflows/external/run</c>) will honor this client for that URL. See
    /// <see cref="Domain.Entities.ApiClientReturnUrl"/>'s remarks.</summary>
    [HttpPost("{id:guid}/return-urls")]
    [ProducesResponseType(typeof(ApiClientDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AddReturnUrl(
        Guid id, [FromBody] AddApiClientReturnUrlRequest request, CancellationToken cancellationToken)
    {
        var client = await _service.AddReturnUrlAsync(id, request, cancellationToken);

        return CreatedAtAction(nameof(GetPaged), null, client);
    }

    [HttpDelete("{id:guid}/return-urls/{returnUrlId:guid}")]
    [ProducesResponseType(typeof(ApiClientDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveReturnUrl(Guid id, Guid returnUrlId, CancellationToken cancellationToken)
    {
        var client = await _service.RemoveReturnUrlAsync(id, returnUrlId, cancellationToken);

        return Ok(client);
    }
}
