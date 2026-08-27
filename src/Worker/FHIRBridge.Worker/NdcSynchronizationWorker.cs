using FHIRBridge.Application.Abstractions.Terminology;
using FHIRBridge.Application.Services;
using FHIRBridge.Application.Services.Terminology;

namespace FHIRBridge.Worker;

/// <summary>Polls hourly and runs the shared, idempotent NDC synchronization pipeline at its configured local
/// time. openFDA's NDC Directory updates daily, so — unlike LOINC/RxNorm — Frequency here also supports Daily.</summary>
public sealed class NdcSynchronizationWorker : BackgroundService
{
    private static readonly TerminologySyncScheduleConfig ScheduleConfig =
        new("Terminology:Ndc", "04:00", TerminologySyncFrequency.Daily);

    private readonly IServiceScopeFactory _scopes;
    private readonly ITerminologySyncScheduleEvaluator _scheduleEvaluator;
    private readonly ILogger<NdcSynchronizationWorker> _logger;

    public NdcSynchronizationWorker(IServiceScopeFactory scopes, ITerminologySyncScheduleEvaluator scheduleEvaluator, ILogger<NdcSynchronizationWorker> logger)
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
                    var result = await scope.ServiceProvider.GetRequiredService<INdcSynchronizationService>().SynchronizeAsync(stoppingToken);
                    await scope.ServiceProvider.GetRequiredService<ISystemSettingsService>().SetAsync("Terminology:Ndc:LastRunUtc", DateTime.UtcNow.ToString("O"), null, stoppingToken);
                    _logger.LogInformation("NDC synchronization completed: {Version}, {Count} products.", result.Version, result.ImportedConceptCount);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    _logger.LogError(ex, "Scheduled NDC synchronization failed.");
                }
            }
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }
}
