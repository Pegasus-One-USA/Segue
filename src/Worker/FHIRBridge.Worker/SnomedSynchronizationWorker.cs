using FHIRBridge.Application.Abstractions.Terminology;
using FHIRBridge.Application.Services;
using FHIRBridge.Application.Services.Terminology;

namespace FHIRBridge.Worker;

/// <summary>Polls hourly and runs the shared, idempotent SNOMED CT synchronization pipeline at its configured local
/// time. SNOMED CT US Edition releases twice yearly (March and September), so this checks for the 1st of either
/// month rather than every month like RxNorm's worker.</summary>
public sealed class SnomedSynchronizationWorker : BackgroundService
{
    private static readonly TerminologySyncScheduleConfig ScheduleConfig = new(
        "Terminology:Snomed", "03:00", DefaultFrequency: null,
        FixedCadence: now => now.Day == 1 && (now.Month == 3 || now.Month == 9));

    private readonly IServiceScopeFactory _scopes;
    private readonly ITerminologySyncScheduleEvaluator _scheduleEvaluator;
    private readonly ILogger<SnomedSynchronizationWorker> _logger;

    public SnomedSynchronizationWorker(IServiceScopeFactory scopes, ITerminologySyncScheduleEvaluator scheduleEvaluator, ILogger<SnomedSynchronizationWorker> logger)
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
                    var result = await scope.ServiceProvider.GetRequiredService<ISnomedSynchronizationService>().SynchronizeAsync(stoppingToken);
                    await scope.ServiceProvider.GetRequiredService<ISystemSettingsService>().SetAsync("Terminology:Snomed:LastRunUtc", DateTime.UtcNow.ToString("O"), null, stoppingToken);
                    _logger.LogInformation("SNOMED CT synchronization completed: {Version}, {Count} concepts.", result.Version, result.ImportedConceptCount);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    _logger.LogError(ex, "Scheduled SNOMED CT synchronization failed.");
                }
            }
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }
}
