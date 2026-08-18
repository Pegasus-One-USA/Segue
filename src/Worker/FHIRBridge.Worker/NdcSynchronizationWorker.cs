using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Terminology;

namespace FHIRBridge.Worker;

/// <summary>Polls hourly and runs the shared, idempotent NDC synchronization pipeline at its configured local
/// time. openFDA's NDC Directory updates daily, so — unlike LOINC/RxNorm — Frequency here also supports Daily.</summary>
public sealed class NdcSynchronizationWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ISystemSettingsCache _settings;
    private readonly ILogger<NdcSynchronizationWorker> _logger;
    private DateOnly? _lastRunDate;

    public NdcSynchronizationWorker(IServiceScopeFactory scopes, ISystemSettingsCache settings, ILogger<NdcSynchronizationWorker> logger)
        => (_scopes, _settings, _logger) = (scopes, settings, logger);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (await _settings.GetBoolAsync("Terminology:Ndc:SchedulerEnabled", false, stoppingToken))
            {
                var now = DateTimeOffset.Now;
                var frequency = await _settings.GetStringAsync("Terminology:Ndc:Frequency", "Daily", stoppingToken);
                var configured = await _settings.GetStringAsync("Terminology:Ndc:ExecutionTime", "04:00", stoppingToken);
                var dueTime = TimeOnly.TryParse(configured, out var time) ? time : new TimeOnly(4, 0);
                var due = now.TimeOfDay >= dueTime.ToTimeSpan() && _lastRunDate != DateOnly.FromDateTime(now.DateTime)
                    && (string.Equals(frequency, "Daily", StringComparison.OrdinalIgnoreCase) || now.DayOfWeek == DayOfWeek.Monday);
                if (due)
                {
                    try
                    {
                        using var scope = _scopes.CreateScope();
                        var result = await scope.ServiceProvider.GetRequiredService<INdcSynchronizationService>().SynchronizeAsync(stoppingToken);
                        _lastRunDate = DateOnly.FromDateTime(now.DateTime);
                        _logger.LogInformation("NDC synchronization completed: {Version}, {Count} products.", result.Version, result.ImportedConceptCount);
                    }
                    catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                    {
                        _logger.LogError(ex, "Scheduled NDC synchronization failed.");
                    }
                }
            }
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }
}
