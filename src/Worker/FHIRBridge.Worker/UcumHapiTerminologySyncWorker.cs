using FHIRBridge.Application.Services;
using FHIRBridge.Application.Services.Terminology;
using FHIRBridge.Infrastructure.Terminology.Hapi;

namespace FHIRBridge.Worker;

/// <summary>
/// Polls hourly and, when due, automatically downloads the official UCUM specification and loads it
/// into the embedded HAPI terminology server. Mirrors <see cref="CvxHapiTerminologySyncWorker"/>
/// exactly. Disabled by default via Terminology:UcumHapi:SchedulerEnabled.
/// </summary>
public sealed class UcumHapiTerminologySyncWorker : BackgroundService
{
    private static readonly TerminologySyncScheduleConfig ScheduleConfig = new("Terminology:UcumHapi", "04:00");

    private readonly IServiceScopeFactory _scopes;
    private readonly ITerminologySyncScheduleEvaluator _scheduleEvaluator;
    private readonly ILogger<UcumHapiTerminologySyncWorker> _logger;

    public UcumHapiTerminologySyncWorker(
        IServiceScopeFactory scopes,
        ITerminologySyncScheduleEvaluator scheduleEvaluator,
        ILogger<UcumHapiTerminologySyncWorker> logger) =>
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
                        .GetRequiredService<IHapiUcumTerminologySyncService>()
                        .SyncAsync(stoppingToken);
                    await scope.ServiceProvider.GetRequiredService<ISystemSettingsService>()
                        .SetAsync("Terminology:UcumHapi:LastRunUtc", DateTime.UtcNow.ToString("O"), null, stoppingToken);
                    _logger.LogInformation(
                        "UCUM HAPI terminology sync completed: {Total} codes, {Elapsed}.",
                        result.TotalConceptCount,
                        result.Duration);
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Scheduled UCUM HAPI terminology sync failed.");
            }

            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }
}
