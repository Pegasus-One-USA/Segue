using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Fhir;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

/// <summary>
/// Which resource types an EHR vendor accepts writes for (<see cref="EhrWriteCapabilities"/>). Read by the source
/// connection form, to offer Write access only where it can be used, and by the EHR Write-Back destination form, to
/// filter its resource picker. Static product data with no PHI, so any signed-in user may read it.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/ehr-write-capabilities")]
public sealed class EhrWriteCapabilitiesController : ControllerBase
{
    private readonly ISystemSettingsCache _settings;

    public EhrWriteCapabilitiesController(ISystemSettingsCache settings)
    {
        _settings = settings;
    }

    /// <summary>The vendor's write capabilities; an unknown or missing vendor gets an empty list, never an error,
    /// because "cannot be written to" is the answer for it.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(EhrWriteCapabilitiesDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get([FromQuery] string? vendor, CancellationToken cancellationToken)
    {
        var profile = EhrWriteCapabilities.TryParseVendor(vendor, out var parsed)
            ? EhrWriteCapabilities.VendorProfile(parsed)
            : null;
        var cloneModeEnabled = await _settings.GetBoolAsync(
            EhrWriteBackSettings.CloneModeEnabledKey, EhrWriteBackSettings.CloneModeEnabledDefault, cancellationToken);
        var dryRunEnabled = await _settings.GetBoolAsync(
            EhrWriteBackSettings.DryRunEnabledKey, EhrWriteBackSettings.DryRunEnabledDefault, cancellationToken);
        var capabilities = (profile?.Capabilities ?? [])
            .Select(capability => new EhrWriteCapabilityDto(
                capability.ResourceType,
                capability.Operations.Order().Select(operation => operation.ToString()).ToList(),
                capability.VendorApiId,
                capability.Variant,
                capability.RequiresEncounter,
                capability.OptInOnly,
                capability.AllowedApplicationTypes.Order().Select(type => type.ToString()).ToList(),
                capability.LiveWriteSupported,
                capability.RequiresVendorActivation,
                capability.CreatesHolderEncounter,
                capability.RequiresVariantOptIn,
                capability.RequiresPatient,
                capability.RequiresTargetReferences))
            .ToList();

        return Ok(new EhrWriteCapabilitiesDto(
            profile?.Vendor.ToString() ?? vendor,
            profile?.SupportsPatientMatch ?? false,
            cloneModeEnabled,
            dryRunEnabled,
            capabilities,
            profile is not null && profile.Vendor == EhrWriteCapabilities.TestServerType
                ? EhrWriteCapabilities.TestableVendors.Select(v => v.ToString()).ToList()
                : []));
    }
}
