using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using FHIRBridge.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

/// <summary>
/// SuperAdmin-only management of runtime-editable system settings — same sensitivity/rationale as
/// AllowedCorsOriginsController: a global (non-tenant-scoped) knob too broad for the general Admin role.
/// </summary>
[ApiController]
[Authorize(Policy = AuthorizationPolicies.SuperAdminOnly)]
[Route("api/v1/system/settings")]
public sealed class SystemSettingsController : ControllerBase
{
    private readonly ISystemSettingsService _service;

    public SystemSettingsController(ISystemSettingsService service)
    {
        _service = service;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<SystemSettingDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll(CancellationToken cancellationToken)
    {
        var settings = await _service.GetAllAsync(cancellationToken);
        return Ok(settings);
    }

    [HttpPut("{key}")]
    [ProducesResponseType(typeof(SystemSettingDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Set(
        string key, [FromBody] SetSystemSettingRequest request, CancellationToken cancellationToken)
    {
        var setting = await _service.SetAsync(key, request.Value, request.Description, cancellationToken);
        return Ok(setting);
    }

    [HttpDelete("{key}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(string key, CancellationToken cancellationToken)
    {
        await _service.DeleteAsync(key, cancellationToken);
        return NoContent();
    }
}
