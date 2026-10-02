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
}
