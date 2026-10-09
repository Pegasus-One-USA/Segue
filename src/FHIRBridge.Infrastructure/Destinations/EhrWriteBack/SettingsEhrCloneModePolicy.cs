using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Services;

namespace FHIRBridge.Infrastructure.Destinations.EhrWriteBack;

/// <summary>Reads <see cref="EhrWriteBackSettings.CloneModeEnabledKey"/> from system settings. See
/// <see cref="IEhrCloneModePolicy"/>.</summary>
public sealed class SettingsEhrCloneModePolicy : IEhrCloneModePolicy
{
    private readonly ISystemSettingsCache _settings;

    public SettingsEhrCloneModePolicy(ISystemSettingsCache settings)
    {
        _settings = settings;
    }

    public Task<bool> IsCloneModeEnabledAsync(CancellationToken cancellationToken) =>
        _settings.GetBoolAsync(EhrWriteBackSettings.CloneModeEnabledKey, EhrWriteBackSettings.CloneModeEnabledDefault, cancellationToken);
}
