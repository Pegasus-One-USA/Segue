using System.Security.Cryptography;
using FHIRBridge.Application.Abstractions.Security;
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
    private readonly IProvisionedSecretDecryptor _secretDecryptor;

    public SystemSettingsController(ISystemSettingsService service, IProvisionedSecretDecryptor secretDecryptor)
    {
        _service = service;
        _secretDecryptor = secretDecryptor;
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

    /// <summary>Saves multiple settings in one call — e.g. every key in a General Settings group
    /// (AlertEvaluation:Enabled, AlertEvaluation:IntervalSeconds, ...) from one Edit dialog's single
    /// Update button, instead of one PUT per field.</summary>
    [HttpPut("batch")]
    [ProducesResponseType(typeof(IReadOnlyList<SystemSettingDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> SetBatch([FromBody] BatchSetSystemSettingsRequest request, CancellationToken cancellationToken)
    {
        var results = new List<SystemSettingDto>(request.Items.Count);
        foreach (var item in request.Items)
        {
            results.Add(await _service.SetAsync(item.Key, item.Value, item.Description, cancellationToken));
        }

        return Ok(results);
    }

    [HttpDelete("{key}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(string key, CancellationToken cancellationToken)
    {
        await _service.DeleteAsync(key, cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// Recovery tool: decrypts a raw <c>ProvisionedSecrets.ProtectedValue</c> blob (e.g. copied from this same
    /// instance's database) back to its plaintext — typically a private key PEM. Only ever works for a value
    /// encrypted by <em>this instance's own</em> Data Protection key ring; a value copied from a different
    /// FHIRBridge deployment will fail here (see <see cref="IProvisionedSecretDecryptor"/>'s remarks) — decrypt it
    /// on/with that deployment's own key ring instead.
    /// </summary>
    [HttpPost("decrypt-provisioned-secret")]
    [ProducesResponseType(typeof(DecryptProvisionedSecretResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult DecryptProvisionedSecret([FromBody] DecryptProvisionedSecretRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ProtectedValue))
        {
            return BadRequest(new { error = "invalid_request", error_description = "protectedValue is required." });
        }

        try
        {
            var plaintext = _secretDecryptor.Decrypt(request.ProtectedValue);
            return Ok(new DecryptProvisionedSecretResponse(plaintext));
        }
        catch (CryptographicException)
        {
            return BadRequest(new
            {
                error = "decryption_failed",
                error_description = "Could not decrypt this value with this instance's own Data Protection key " +
                    "ring. A ProtectedValue copied from a different FHIRBridge deployment cannot be decrypted here.",
            });
        }
    }
}
