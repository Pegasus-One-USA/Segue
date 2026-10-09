namespace FHIRBridge.Application.Abstractions.Destinations;

/// <summary>
/// The QA-only <see cref="FHIRBridge.Application.Services.EhrWriteBackSettings.CloneModeEnabledKey"/> system setting.
/// A destination asking for clone mode while it is off is refused, not silently run normally.
/// </summary>
/// <remarks>
/// There is no installation-wide live-write release any more. A record is sent live when the code supports live
/// writes for its type (<see cref="FHIRBridge.Domain.Fhir.EhrWriteCapability.LiveWriteSupported"/>), the destination
/// selects the type and is not a dry run; who may switch a destination off dry run, and run it, is decided by the
/// EHR Write-Back permissions (ehrwriteback.edit, ehrwriteback.execute).
/// </remarks>
public interface IEhrCloneModePolicy
{
    Task<bool> IsCloneModeEnabledAsync(CancellationToken cancellationToken);
}
