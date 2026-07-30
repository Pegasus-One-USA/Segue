namespace FHIRBridge.Application.Abstractions.Caching;

/// <summary>
/// Process-wide read cache for <see cref="FHIRBridge.Domain.Entities.SystemSetting"/> rows. Safe to
/// inject into singletons (hosted/background services) since it resolves the repository lazily through
/// a scope. Typed getters fall back to the caller-supplied appsettings-derived default when no DB row
/// exists for the key, or when the stored value fails to parse.
/// </summary>
public interface ISystemSettingsCache
{
    Task<string> GetStringAsync(string key, string defaultValue, CancellationToken cancellationToken);

    Task<bool> GetBoolAsync(string key, bool defaultValue, CancellationToken cancellationToken);

    Task<int> GetIntAsync(string key, int defaultValue, CancellationToken cancellationToken);

    Task<double> GetDoubleAsync(string key, double defaultValue, CancellationToken cancellationToken);

    void Invalidate();
}
