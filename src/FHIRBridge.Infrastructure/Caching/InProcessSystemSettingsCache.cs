using System.Data.Common;
using System.Globalization;
using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Observability.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace FHIRBridge.Infrastructure.Caching;

/// <summary>
/// Single-instance in-process cache: correct for the current one-VM deployment, same limitation as
/// <see cref="InProcessAllowedCorsOriginsCache"/> if the API ever scales to multiple instances.
/// </summary>
public sealed class InProcessSystemSettingsCache : ISystemSettingsCache
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<InProcessSystemSettingsCache> _logger;
    private volatile IReadOnlyDictionary<string, string>? _cached;

    public InProcessSystemSettingsCache(
        IServiceScopeFactory scopeFactory,
        ILogger<InProcessSystemSettingsCache>? logger = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger ?? NullLogger<InProcessSystemSettingsCache>.Instance;
    }

    public async Task<string> GetStringAsync(string key, string defaultValue, CancellationToken cancellationToken)
    {
        var snapshot = await GetSnapshotAsync(cancellationToken);
        return snapshot.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : defaultValue;
    }

    public async Task<bool> GetBoolAsync(string key, bool defaultValue, CancellationToken cancellationToken)
    {
        var snapshot = await GetSnapshotAsync(cancellationToken);
        return snapshot.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed)
            ? parsed
            : defaultValue;
    }

    public async Task<int> GetIntAsync(string key, int defaultValue, CancellationToken cancellationToken)
    {
        var snapshot = await GetSnapshotAsync(cancellationToken);
        return snapshot.TryGetValue(key, out var value)
            && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : defaultValue;
    }

    public async Task<double> GetDoubleAsync(string key, double defaultValue, CancellationToken cancellationToken)
    {
        var snapshot = await GetSnapshotAsync(cancellationToken);
        return snapshot.TryGetValue(key, out var value)
            && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : defaultValue;
    }

    public void Invalidate() => _cached = null;

    private async Task<IReadOnlyDictionary<string, string>> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var snapshot = _cached;
        if (snapshot is not null)
        {
            return snapshot;
        }

        using var scope = _scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ISystemSettingRepository>();

        IReadOnlyList<FHIRBridge.Domain.Entities.SystemSetting> settings;
        try
        {
            settings = await repository.GetAllAsync(cancellationToken);
        }
        catch (DbException exception)
        {
            // The SystemSettings table doesn't exist yet — e.g. this read happens during startup, before
            // dbContext.Database.Migrate() has run. Don't cache the miss: once migrated, the next call
            // succeeds and every caller falls back to its own compiled-in/appsettings default meanwhile.
            //
            // Logged because the consequence is invisible otherwise: every DB-backed setting silently reverts to
            // its compiled-in default for this call, so any value an operator changed in the portal is ignored
            // while this persists. Expected exactly once during a first-run migration; anything beyond that is a
            // real database problem.
            _logger.LogWarning(
                LogEvents.SystemSettingsUnavailable,
                exception,
                "SystemSettings could not be read ({FailureReason}); every DB-backed setting is falling back to its "
                + "compiled-in default for this call, so portal-configured values are ignored. This is expected "
                + "only before the first migration has run.",
                exception.Message);

            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var merged = settings.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
        _cached = merged;
        return merged;
    }
}
