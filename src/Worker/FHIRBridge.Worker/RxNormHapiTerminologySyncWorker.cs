using FHIRBridge.Application.Services;
using FHIRBridge.Application.Services.Terminology;
using FHIRBridge.Infrastructure.Terminology.Hapi;

namespace FHIRBridge.Worker;

/// <summary>
/// Polls hourly and, when due, automatically downloads the credentialed RxNorm release (via the
/// existing UMLS/UTS API key) and loads it into the embedded HAPI terminology server. Mirrors
/// <see cref="LoincHapiTerminologySyncWorker"/> exactly. Disabled by default via
/// Terminology:RxNormHapi:SchedulerEnabled.
/// </summary>
public sealed class RxNormHapiTerminologySyncWorker : BackgroundService
{
    private static readonly TerminologySyncScheduleConfig ScheduleConfig = new("Terminology:RxNormHapi", "04:30");

    private readonly IServiceScopeFactory _scopes;
    private readonly ITerminologySyncScheduleEvaluator _scheduleEvaluator;
    private readonly ILogger<RxNormHapiTerminologySyncWorker> _logger;

    public RxNormHapiTerminologySyncWorker(
        IServiceScopeFactory scopes,
        ITerminologySyncScheduleEvaluator scheduleEvaluator,
        ILogger<RxNormHapiTerminologySyncWorker> logger) =>
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
                        .GetRequiredService<IHapiRxNormTerminologySyncService>()
                        .SyncAsync(stoppingToken);
                    await scope.ServiceProvider.GetRequiredService<ISystemSettingsService>()
                        .SetAsync("Terminology:RxNormHapi:LastRunUtc", DateTime.UtcNow.ToString("O"), null, stoppingToken);
                    _logger.LogInformation(
                        "RxNorm HAPI terminology sync completed: {Version}, {Total} codes, {Elapsed}.",
                        result.Version,
                        result.TotalConceptCount,
                        result.Duration);
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Scheduled RxNorm HAPI terminology sync failed.");
            }

            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }
}
