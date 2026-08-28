using FHIRBridge.Application.Services;
using FHIRBridge.Application.Services.Terminology;
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
    private static readonly TerminologySyncScheduleConfig ScheduleConfig = new("Terminology:Icd10Hapi", "03:00");

    private readonly IServiceScopeFactory _scopes;
    private readonly ITerminologySyncScheduleEvaluator _scheduleEvaluator;
    private readonly ILogger<Icd10HapiTerminologySyncWorker> _logger;

    public Icd10HapiTerminologySyncWorker(
        IServiceScopeFactory scopes,
        ITerminologySyncScheduleEvaluator scheduleEvaluator,
        ILogger<Icd10HapiTerminologySyncWorker> logger) =>
        (_scopes, _scheduleEvaluator, _logger) = (scopes, scheduleEvaluator, logger);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await _scheduleEvaluator.IsDueAsync(ScheduleConfig, DateTimeOffset.Now, stoppingToken))
                {
                    using var scope = _scopes.CreateScope();
                    var result = await scope.ServiceProvider
                        .GetRequiredService<IHapiIcd10TerminologySyncService>()
                        .SyncAsync(stoppingToken);
                    await scope.ServiceProvider.GetRequiredService<ISystemSettingsService>()
                        .SetAsync("Terminology:Icd10Hapi:LastRunUtc", DateTime.UtcNow.ToString("O"), null, stoppingToken);
                    _logger.LogInformation(
                        "ICD-10-CM HAPI terminology sync completed: {Year}, {Total} codes ({Billable} billable), {Elapsed}.",
                        result.ReleaseYear,
                        result.TotalConceptCount,
                        result.BillableConceptCount,
                        result.Duration);
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
