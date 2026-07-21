using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using FHIRBridge.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

/// <summary>
/// SuperAdmin-only management of the app-level signing secrets (JWT signing key, download-link signing
/// secret) — auto-generated on first boot (see AppSecretProvisioner), exposed here only for on-demand
/// rotation. The raw value is never returned by either endpoint; this surface is metadata + regenerate only.
/// </summary>
[ApiController]
[Authorize(Policy = AuthorizationPolicies.SuperAdminOnly)]
[Route("api/v1/system/app-secrets")]
public sealed class AppSecretsController : ControllerBase
{
    private readonly IAppSecretsAdminService _service;

    public AppSecretsController(IAppSecretsAdminService service)
    {
        _service = service;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<AppSecretDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll(CancellationToken cancellationToken)
    {
        var secrets = await _service.GetAllAsync(cancellationToken);

        return Ok(secrets);
    }

    [HttpPost("{secretName}/regenerate")]
    [ProducesResponseType(typeof(AppSecretDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Regenerate(string secretName, CancellationToken cancellationToken)
    {
        var secret = await _service.RegenerateAsync(secretName, cancellationToken);

        return Ok(secret);
    }
}
