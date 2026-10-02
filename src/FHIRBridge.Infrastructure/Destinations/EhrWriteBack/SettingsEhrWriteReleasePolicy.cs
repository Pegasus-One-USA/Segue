using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Infrastructure.Destinations.EhrWriteBack;

/// <summary>Reads the live-write release from the <see cref="EhrWriteBackSettings.LiveWriteTypesKey"/> system
/// setting, intersected with what the code supports. See <see cref="IEhrWriteReleasePolicy"/>.</summary>
public sealed class SettingsEhrWriteReleasePolicy : IEhrWriteReleasePolicy
{
    private readonly ISystemSettingsCache _settings;

    public SettingsEhrWriteReleasePolicy(ISystemSettingsCache settings)
    {
        _settings = settings;
    }

    public async Task<IReadOnlySet<string>> GetReleasedResourceTypesAsync(SourceSystemType vendor, CancellationToken cancellationToken)
    {
        var value = await _settings.GetStringAsync(
            EhrWriteBackSettings.LiveWriteTypesKey, EhrWriteBackSettings.LiveWriteTypesDefault, cancellationToken);
        return EhrWriteBackSettings.ReleasedResourceTypes(value, vendor);
    }

    public Task<bool> IsCloneModeEnabledAsync(CancellationToken cancellationToken) =>
        _settings.GetBoolAsync(EhrWriteBackSettings.CloneModeEnabledKey, EhrWriteBackSettings.CloneModeEnabledDefault, cancellationToken);
}
