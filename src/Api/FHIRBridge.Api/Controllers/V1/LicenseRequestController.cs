using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Licensing;
using FHIRBridge.Application.Security;
using FHIRBridge.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace FHIRBridge.Api.Controllers.V1;

/// <summary>
/// This install's own outbound license request — see <see cref="ILicenseRequestService"/>'s remarks for the
/// full one-request-per-install lifecycle. Same UnifiedAdmin gate as <see cref="LicenseController"/>.
/// </summary>
[ApiController]
[Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
[Route("api/v1/license-request")]
public sealed class LicenseRequestController : ControllerBase
{
    private readonly ILicenseRequestService _service;
    private readonly ISystemSettingsService _systemSettingsService;
    private readonly ISystemSettingsCache _systemSettingsCache;
    private readonly IConfiguration _configuration;

    public LicenseRequestController(
        ILicenseRequestService service,
        ISystemSettingsService systemSettingsService,
        ISystemSettingsCache systemSettingsCache,
        IConfiguration configuration)
    {
        _service = service;
        _systemSettingsService = systemSettingsService;
        _systemSettingsCache = systemSettingsCache;
        _configuration = configuration;
    }

    [HttpGet]
    [ProducesResponseType(typeof(LicenseRequestStatusResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken) =>
        Ok(await _service.GetAsync(cancellationToken));

    [HttpPost]
    [ProducesResponseType(typeof(LicenseRequestStatusResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create(
        [FromBody] CreateLicenseRequestRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _service.CreateAndSubmitAsync(
                new LicenseRequestInput(
                    request.ClientName, request.Email, request.CompanyName, request.Address, request.PhoneNumber),
                HttpContext.Request.Host.Value,
                cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = "invalid_request", error_description = ex.Message });
        }
    }

    [HttpPost("resubmit")]
    [ProducesResponseType(typeof(LicenseRequestStatusResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Resubmit(CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _service.ResubmitAsync(cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = "invalid_request", error_description = ex.Message });
        }
    }

    /// <summary>Narrow, license-gate-allowlisted read of just <c>License:LicensorApplicationUrl</c> —
    /// exists so the portal's License screen never needs the general-purpose (and NOT allowlisted)
    /// <c>GET /api/v1/system/settings</c>, which would open every other setting to read/write while
    /// unlicensed. See <see cref="LicenseRequestSettingKeys"/>.</summary>
    [HttpGet("licensor-url")]
    [ProducesResponseType(typeof(LicensorApplicationUrlDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetLicensorUrl(CancellationToken cancellationToken)
    {
        var value = await _systemSettingsCache.GetStringAsync(
            LicenseRequestSettingKeys.LicensorApplicationUrl,
            _configuration[LicenseRequestSettingKeys.LicensorApplicationUrl] ?? string.Empty,
            cancellationToken);
        return Ok(new LicensorApplicationUrlDto(value));
    }

    /// <summary>Narrow counterpart to <see cref="GetLicensorUrl"/> — the only system setting the License
    /// screen needs to write while unlicensed. Delegates to <see cref="ISystemSettingsService"/> for the
    /// exact same URL-shape validation <c>SystemSettingsController.Set</c> would apply to this key.</summary>
    [HttpPut("licensor-url")]
    [ProducesResponseType(typeof(LicensorApplicationUrlDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SetLicensorUrl(
        [FromBody] SetLicensorApplicationUrlRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var setting = await _systemSettingsService.SetAsync(
                LicenseRequestSettingKeys.LicensorApplicationUrl, request.Url,
                "Base URL this install posts license requests to.", cancellationToken);
            return Ok(new LicensorApplicationUrlDto(setting.Value));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = "invalid_request", error_description = ex.Message });
        }
    }
}

/// <summary>Body of <c>POST /api/v1/license-request</c>.</summary>
public sealed record CreateLicenseRequestRequest(
    string ClientName, string Email, string? CompanyName, string? Address, string PhoneNumber);

/// <summary>Wire shape for <c>GET</c>/<c>PUT /api/v1/license-request/licensor-url</c>.</summary>
public sealed record LicensorApplicationUrlDto(string Url);

/// <summary>Body of <c>PUT /api/v1/license-request/licensor-url</c>.</summary>
public sealed record SetLicensorApplicationUrlRequest(string Url);
