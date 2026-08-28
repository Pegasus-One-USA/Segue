using FHIRBridge.Application.Abstractions.Terminology;
using FHIRBridge.Application.Services;
using FHIRBridge.Application.Services.Terminology;

namespace FHIRBridge.Worker;

/// <summary>Polls hourly and runs the shared, idempotent UCUM synchronization pipeline at its configured local time.</summary>
public sealed class UcumSynchronizationWorker : BackgroundService
{
    private static readonly TerminologySyncScheduleConfig ScheduleConfig =
        new("Terminology:Ucum", "05:00", TerminologySyncFrequency.Weekly);

    private readonly IServiceScopeFactory _scopes;
    private readonly ITerminologySyncScheduleEvaluator _scheduleEvaluator;
    private readonly ILogger<UcumSynchronizationWorker> _logger;

    public UcumSynchronizationWorker(IServiceScopeFactory scopes, ITerminologySyncScheduleEvaluator scheduleEvaluator, ILogger<UcumSynchronizationWorker> logger)
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
                    var result = await scope.ServiceProvider.GetRequiredService<IUcumSynchronizationService>().SynchronizeAsync(stoppingToken);
                    await scope.ServiceProvider.GetRequiredService<ISystemSettingsService>().SetAsync("Terminology:Ucum:LastRunUtc", DateTime.UtcNow.ToString("O"), null, stoppingToken);
                    _logger.LogInformation("UCUM synchronization completed: {Version}, {Count} units.", result.Version, result.ImportedConceptCount);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    _logger.LogError(ex, "Scheduled UCUM synchronization failed.");
                }
            }
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }
}
