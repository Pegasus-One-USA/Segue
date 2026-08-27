using FHIRBridge.Application.Abstractions.Terminology;
using FHIRBridge.Application.Services;
using FHIRBridge.Application.Services.Terminology;

namespace FHIRBridge.Worker;

/// <summary>Polls hourly and runs the shared, idempotent LOINC synchronization pipeline at its configured local time.</summary>
public sealed class LoincSynchronizationWorker : BackgroundService
{
    private static readonly TerminologySyncScheduleConfig ScheduleConfig = new("Terminology:Loinc", "02:00");

    private readonly IServiceScopeFactory _scopes;
    private readonly ITerminologySyncScheduleEvaluator _scheduleEvaluator;
    private readonly ILogger<LoincSynchronizationWorker> _logger;
    public LoincSynchronizationWorker(IServiceScopeFactory scopes, ITerminologySyncScheduleEvaluator scheduleEvaluator, ILogger<LoincSynchronizationWorker> logger) => (_scopes, _scheduleEvaluator, _logger) = (scopes, scheduleEvaluator, logger);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (await _scheduleEvaluator.IsDueAsync(ScheduleConfig, DateTimeOffset.Now, stoppingToken))
            {
                try
                {
                    using var scope = _scopes.CreateScope();
                    var result = await scope.ServiceProvider.GetRequiredService<ILoincSynchronizationService>().SynchronizeAsync(stoppingToken);
                    await scope.ServiceProvider.GetRequiredService<ISystemSettingsService>().SetAsync("Terminology:Loinc:LastRunUtc", DateTime.UtcNow.ToString("O"), null, stoppingToken);
                    _logger.LogInformation("LOINC synchronization completed: {Version}, {Count} terms, already current: {Current}.", result.Version, result.ImportedConceptCount, result.AlreadyCurrent);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { _logger.LogError(ex, "Scheduled LOINC synchronization failed."); }
            }
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }
}
