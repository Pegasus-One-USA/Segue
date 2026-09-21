using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Licensing;
using FHIRBridge.Application.Security;
using FHIRBridge.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace FHIRBridge.Api.Controllers.V1;

/// <summary>
/// This install's own outbound license requests — see <see cref="ILicenseRequestService"/>'s remarks; any
/// number of them can exist, each independent. Same UnifiedAdmin gate as <see cref="LicenseController"/>.
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
    [ProducesResponseType(typeof(IReadOnlyList<LicenseRequestStatusResult>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken cancellationToken) =>
        Ok(await _service.ListAsync(cancellationToken));

    /// <summary>Always creates a brand-new request — never fails because one already exists, so an
    /// install can submit as many as it needs (e.g. one per renewal, or several in a row while testing).</summary>
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
                ResolveRequestedFrom(request.RequestedFromUrl),
                cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = "invalid_request", error_description = ex.Message });
        }
    }

    /// <summary>Corrects one existing request's stored contact details (e.g. a typo'd email) and
    /// re-submits it to the licensor — never touches its request key. Fails with 400 if <paramref
    /// name="id"/> doesn't match one of this install's own requests.</summary>
    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(LicenseRequestStatusResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Update(
        Guid id, [FromBody] CreateLicenseRequestRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _service.UpdateAsync(
                id,
                new LicenseRequestInput(
                    request.ClientName, request.Email, request.CompanyName, request.Address, request.PhoneNumber),
                ResolveRequestedFrom(request.RequestedFromUrl),
                cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = "invalid_request", error_description = ex.Message });
        }
    }

    /// <summary>The admin's own browser URL at submit time (e.g. <c>https://portal.example.com</c>,
    /// captured client-side as <c>window.location.origin</c>) — the API's own Host header only reflects
    /// where the API itself is bound (e.g. <c>localhost:5000</c>), never the portal's port, so it can't be
    /// used for this. Purely informational for the licensor, never used for any security decision (that's
    /// <c>UniqueKey</c>'s job) — falls back to the Host header only for a caller that didn't supply one.</summary>
    private string? ResolveRequestedFrom(string? requestedFromUrl) =>
        string.IsNullOrWhiteSpace(requestedFromUrl) ? HttpContext.Request.Host.Value : requestedFromUrl.Trim();

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

/// <summary>Body of <c>POST /api/v1/license-request</c>. <see cref="RequestedFromUrl"/> is the admin's own
/// browser origin at submit time, captured client-side — the API can't derive this itself (see
/// <see cref="LicenseRequestController.ResolveRequestedFrom"/>).</summary>
public sealed record CreateLicenseRequestRequest(
    string ClientName, string Email, string? CompanyName, string? Address, string PhoneNumber,
    string? RequestedFromUrl = null);

/// <summary>Wire shape for <c>GET</c>/<c>PUT /api/v1/license-request/licensor-url</c>.</summary>
public sealed record LicensorApplicationUrlDto(string Url);

/// <summary>Body of <c>PUT /api/v1/license-request/licensor-url</c>.</summary>
public sealed record SetLicensorApplicationUrlRequest(string Url);
