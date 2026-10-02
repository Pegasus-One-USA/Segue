using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.Abstractions.Destinations;

/// <summary>
/// Which resource types may be written LIVE to a vendor right now. A type is live only when the code supports it
/// (<see cref="FHIRBridge.Domain.Fhir.EhrWriteCapability.LiveWriteSupported"/>) and an administrator released it
/// (<see cref="FHIRBridge.Application.Services.EhrWriteBackSettings.LiveWriteTypesKey"/>). Everything else is a dry
/// run, whatever the destination asks for.
/// </summary>
public interface IEhrWriteReleasePolicy
{
    Task<IReadOnlySet<string>> GetReleasedResourceTypesAsync(SourceSystemType vendor, CancellationToken cancellationToken);

    /// <summary>The QA-only <see cref="FHIRBridge.Application.Services.EhrWriteBackSettings.CloneModeEnabledKey"/>
    /// system setting. A destination asking for clone mode while it is off is refused, not silently run normally.</summary>
    Task<bool> IsCloneModeEnabledAsync(CancellationToken cancellationToken);
}
