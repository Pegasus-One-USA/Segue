using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using FHIRBridge.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

/// <summary>
/// SuperAdmin-only "SSO Configurations" admin screen — SAML/magic-link fields are DB-backed (via
/// <see cref="ISsoConfigurationsService"/>) and take effect immediately, no restart. Same sensitivity
/// tier as <see cref="SystemSettingsController"/>.
/// </summary>
[ApiController]
[Authorize(Policy = AuthorizationPolicies.SuperAdminOnly)]
[Route("api/v1/system/sso-configurations")]
public sealed class SsoConfigurationsController : ControllerBase
{
    private readonly ISsoConfigurationsService _service;

    public SsoConfigurationsController(ISsoConfigurationsService service)
    {
        _service = service;
    }

    [HttpGet]
    [ProducesResponseType(typeof(SsoConfigurationsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var dto = await _service.GetAsync(cancellationToken);
        return Ok(EnrichWithComputedUrls(dto));
    }

    [HttpPut]
    [ProducesResponseType(typeof(SsoConfigurationsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(
        [FromBody] UpdateSsoConfigurationsRequest request, CancellationToken cancellationToken)
    {
        var dto = await _service.UpdateAsync(request, cancellationToken);
        return Ok(EnrichWithComputedUrls(dto));
    }

    /// <summary>The service layer is HTTP-agnostic — these two URLs depend on the current request's own
    /// scheme/host, so they're filled in here rather than resolved via config, matching how
    /// AuthController.BuildAcsUrl() already derives the ACS URL the same way.</summary>
    private SsoConfigurationsDto EnrichWithComputedUrls(SsoConfigurationsDto dto) => dto with
    {
        SamlMetadataUrl = $"{Request.Scheme}://{Request.Host}/api/v1/auth/saml/metadata",
        SamlAcsUrl = $"{Request.Scheme}://{Request.Host}/api/v1/auth/saml/acs",
    };
}
