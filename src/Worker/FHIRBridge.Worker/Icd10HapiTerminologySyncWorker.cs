using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Infrastructure.Terminology.Hapi;

namespace FHIRBridge.Worker;

/// <summary>
/// Polls hourly and, when due, automatically downloads the official CMS/CDC ICD-10-CM release
/// and loads it into the embedded HAPI terminology server — the "runs on its own, no admin click"
/// counterpart to the manual `tools/TerminologyServerPoc --auto-icd10` run it was proven with.
/// Mirrors <see cref="LoincSynchronizationWorker"/>'s scheduling pattern exactly. Disabled by
/// default (Terminology:Icd10Hapi:SchedulerEnabled) since the hapi-terminology server is a POC
/// addition, not yet part of the default deployment.
/// </summary>
public sealed class Icd10HapiTerminologySyncWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ISystemSettingsCache _settings;
    private readonly ILogger<Icd10HapiTerminologySyncWorker> _logger;
    private DateOnly? _lastRunDate;

    public Icd10HapiTerminologySyncWorker(
        IServiceScopeFactory scopes, ISystemSettingsCache settings, ILogger<Icd10HapiTerminologySyncWorker> logger) =>
        (_scopes, _settings, _logger) = (scopes, settings, logger);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await _settings.GetBoolAsync("Terminology:Icd10Hapi:SchedulerEnabled", false, stoppingToken))
                {
                    var now = DateTimeOffset.Now;
                    var frequency = await _settings.GetStringAsync("Terminology:Icd10Hapi:Frequency", "Monthly", stoppingToken);
                    var configured = await _settings.GetStringAsync("Terminology:Icd10Hapi:ExecutionTime", "03:00", stoppingToken);
                    var dueTime = TimeOnly.TryParse(configured, out var time) ? time : new TimeOnly(3, 0);
                    var due = now.TimeOfDay >= dueTime.ToTimeSpan() && _lastRunDate != DateOnly.FromDateTime(now.DateTime)
                        && (string.Equals(frequency, "Weekly", StringComparison.OrdinalIgnoreCase) || now.Day == 1);

                    if (due)
                    {
                        using var scope = _scopes.CreateScope();
                        var result = await scope.ServiceProvider
                            .GetRequiredService<IHapiIcd10TerminologySyncService>()
                            .SyncAsync(stoppingToken);
                        _lastRunDate = DateOnly.FromDateTime(now.DateTime);
                        _logger.LogInformation(
                            "ICD-10-CM HAPI terminology sync completed: {Year}, {Total} codes ({Billable} billable), {Elapsed}.",
                            result.ReleaseYear,
                            result.TotalConceptCount,
                            result.BillableConceptCount,
                            result.Duration);
                    }
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // Deliberately catches settings-read failures too (e.g. a transient DB timeout during the
                // startup "thundering herd" of many hosted services reading settings at once), not just
                // sync failures — an uncaught exception here silently kills this BackgroundService's loop
                // with nothing logged, which is what happened to the CVX worker's narrower try/catch.
                _logger.LogError(ex, "Scheduled ICD-10-CM HAPI terminology sync failed.");
            }

            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }
}
