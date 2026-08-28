using FHIRBridge.Application.Services;
using FHIRBridge.Application.Services.Terminology;
using FHIRBridge.Infrastructure.Terminology.Hapi;

namespace FHIRBridge.Worker;

/// <summary>
/// Polls hourly and, when due, automatically downloads the official CDC CVX vaccine code table and
/// loads it into the embedded HAPI terminology server. Mirrors <see cref="Icd10HapiTerminologySyncWorker"/>
/// exactly. Disabled by default via Terminology:CvxHapi:SchedulerEnabled.
/// </summary>
public sealed class CvxHapiTerminologySyncWorker : BackgroundService
{
    private static readonly TerminologySyncScheduleConfig ScheduleConfig = new("Terminology:CvxHapi", "03:15");

    private readonly IServiceScopeFactory _scopes;
    private readonly ITerminologySyncScheduleEvaluator _scheduleEvaluator;
    private readonly ILogger<CvxHapiTerminologySyncWorker> _logger;

    public CvxHapiTerminologySyncWorker(
        IServiceScopeFactory scopes,
        ITerminologySyncScheduleEvaluator scheduleEvaluator,
        ILogger<CvxHapiTerminologySyncWorker> logger) =>
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
                        .GetRequiredService<IHapiCvxTerminologySyncService>()
                        .SyncAsync(stoppingToken);
                    await scope.ServiceProvider.GetRequiredService<ISystemSettingsService>()
                        .SetAsync("Terminology:CvxHapi:LastRunUtc", DateTime.UtcNow.ToString("O"), null, stoppingToken);
                    _logger.LogInformation(
                        "CVX HAPI terminology sync completed: {Total} codes ({Active} active), {Elapsed}.",
                        result.TotalConceptCount,
                        result.ActiveConceptCount,
                        result.Duration);
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // Deliberately catches settings-read failures too (e.g. a transient DB timeout during the
                // startup "thundering herd" of many hosted services reading settings at once), not just
                // sync failures — a narrower try/catch here once let an uncaught exception silently kill
                // this loop with nothing logged.
                _logger.LogError(ex, "Scheduled CVX HAPI terminology sync failed.");
            }

            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }
}
