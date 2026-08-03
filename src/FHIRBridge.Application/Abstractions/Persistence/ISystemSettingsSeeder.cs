namespace FHIRBridge.Application.Abstractions.Persistence;

/// <summary>
/// Seeds a SystemSetting row for every DB-backed configuration key, using the value currently
/// effective from appsettings/env (or the compiled-in default when unset) — so deploying this feature
/// changes nothing behaviorally. Only inserts rows that don't already exist; never overwrites a value
/// an admin has since edited via SystemSettingsController.
/// </summary>
public interface ISystemSettingsSeeder
{
    Task EnsureSeededAsync(CancellationToken cancellationToken);
}
