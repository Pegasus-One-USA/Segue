using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Terminology;

namespace FHIRBridge.Worker;

/// <summary>Polls hourly and runs the shared, idempotent SNOMED CT synchronization pipeline at its configured local
/// time. SNOMED CT US Edition releases twice yearly (March and September), so this checks for the 1st of either
/// month rather than every month like RxNorm's worker.</summary>
public sealed class SnomedSynchronizationWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ISystemSettingsCache _settings;
    private readonly ILogger<SnomedSynchronizationWorker> _logger;
    private DateOnly? _lastRunDate;

    public SnomedSynchronizationWorker(IServiceScopeFactory scopes, ISystemSettingsCache settings, ILogger<SnomedSynchronizationWorker> logger)
        => (_scopes, _settings, _logger) = (scopes, settings, logger);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (await _settings.GetBoolAsync("Terminology:Snomed:SchedulerEnabled", false, stoppingToken))
            {
                var now = DateTimeOffset.Now;
                var configured = await _settings.GetStringAsync("Terminology:Snomed:ExecutionTime", "03:00", stoppingToken);
                var dueTime = TimeOnly.TryParse(configured, out var time) ? time : new TimeOnly(3, 0);
                var due = now.TimeOfDay >= dueTime.ToTimeSpan() && _lastRunDate != DateOnly.FromDateTime(now.DateTime)
                    && now.Day == 1 && (now.Month == 3 || now.Month == 9);
                if (due)
                {
                    try
                    {
                        using var scope = _scopes.CreateScope();
                        var result = await scope.ServiceProvider.GetRequiredService<ISnomedSynchronizationService>().SynchronizeAsync(stoppingToken);
                        _lastRunDate = DateOnly.FromDateTime(now.DateTime);
                        _logger.LogInformation("SNOMED CT synchronization completed: {Version}, {Count} concepts.", result.Version, result.ImportedConceptCount);
                    }
                    catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                    {
                        _logger.LogError(ex, "Scheduled SNOMED CT synchronization failed.");
                    }
                }
            }
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }
}
