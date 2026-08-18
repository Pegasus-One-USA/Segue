using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Terminology;

namespace FHIRBridge.Worker;

/// <summary>Polls hourly and runs the shared, idempotent RxNorm synchronization pipeline at its configured local
/// time. RxNorm has a fixed monthly cadence (NLM releases on the first Monday of each month), so unlike LOINC's
/// worker there is no Frequency setting to read — only whether the scheduler is enabled and when to run.</summary>
public sealed class RxNormSynchronizationWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ISystemSettingsCache _settings;
    private readonly ILogger<RxNormSynchronizationWorker> _logger;
    private DateOnly? _lastRunDate;

    public RxNormSynchronizationWorker(IServiceScopeFactory scopes, ISystemSettingsCache settings, ILogger<RxNormSynchronizationWorker> logger)
        => (_scopes, _settings, _logger) = (scopes, settings, logger);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (await _settings.GetBoolAsync("Terminology:RxNorm:SchedulerEnabled", false, stoppingToken))
            {
                var now = DateTimeOffset.Now;
                var configured = await _settings.GetStringAsync("Terminology:RxNorm:ExecutionTime", "02:00", stoppingToken);
                var dueTime = TimeOnly.TryParse(configured, out var time) ? time : new TimeOnly(2, 0);
                var due = now.TimeOfDay >= dueTime.ToTimeSpan() && _lastRunDate != DateOnly.FromDateTime(now.DateTime) && now.Day == 1;
                if (due)
                {
                    try
                    {
                        using var scope = _scopes.CreateScope();
                        var result = await scope.ServiceProvider.GetRequiredService<IRxNormSynchronizationService>().SynchronizeAsync(stoppingToken);
                        _lastRunDate = DateOnly.FromDateTime(now.DateTime);
                        _logger.LogInformation("RxNorm synchronization completed: {Version}, {Count} concepts.", result.Version, result.ImportedConceptCount);
                    }
                    catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                    {
                        _logger.LogError(ex, "Scheduled RxNorm synchronization failed.");
                    }
                }
            }
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }
}
