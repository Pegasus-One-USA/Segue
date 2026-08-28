using FHIRBridge.Application.Abstractions.Terminology;
using FHIRBridge.Application.Services;
using FHIRBridge.Application.Services.Terminology;

namespace FHIRBridge.Worker;

/// <summary>Polls hourly and runs the shared, idempotent RxNorm synchronization pipeline at its configured local
/// time. RxNorm has a fixed monthly cadence (NLM releases on the first Monday of each month), so unlike LOINC's
/// worker there is no Frequency setting to read — only whether the scheduler is enabled and when to run.</summary>
public sealed class RxNormSynchronizationWorker : BackgroundService
{
    private static readonly TerminologySyncScheduleConfig ScheduleConfig = new(
        "Terminology:RxNorm", "02:00", DefaultFrequency: null, FixedCadence: now => now.Day == 1);

    private readonly IServiceScopeFactory _scopes;
    private readonly ITerminologySyncScheduleEvaluator _scheduleEvaluator;
    private readonly ILogger<RxNormSynchronizationWorker> _logger;

    public RxNormSynchronizationWorker(IServiceScopeFactory scopes, ITerminologySyncScheduleEvaluator scheduleEvaluator, ILogger<RxNormSynchronizationWorker> logger)
        => (_scopes, _scheduleEvaluator, _logger) = (scopes, scheduleEvaluator, logger);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (await _scheduleEvaluator.IsDueAsync(ScheduleConfig, DateTimeOffset.Now, stoppingToken))
            {
                try
                {
                    using var scope = _scopes.CreateScope();
                    var result = await scope.ServiceProvider.GetRequiredService<IRxNormSynchronizationService>().SynchronizeAsync(stoppingToken);
                    await scope.ServiceProvider.GetRequiredService<ISystemSettingsService>().SetAsync("Terminology:RxNorm:LastRunUtc", DateTime.UtcNow.ToString("O"), null, stoppingToken);
                    _logger.LogInformation("RxNorm synchronization completed: {Version}, {Count} concepts.", result.Version, result.ImportedConceptCount);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    _logger.LogError(ex, "Scheduled RxNorm synchronization failed.");
                }
            }
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }
}
