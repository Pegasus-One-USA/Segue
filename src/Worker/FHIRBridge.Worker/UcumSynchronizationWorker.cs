using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Terminology;

namespace FHIRBridge.Worker;

/// <summary>Polls hourly and runs the shared, idempotent UCUM synchronization pipeline at its configured local time.</summary>
public sealed class UcumSynchronizationWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ISystemSettingsCache _settings;
    private readonly ILogger<UcumSynchronizationWorker> _logger;
    private DateOnly? _lastRunDate;

    public UcumSynchronizationWorker(IServiceScopeFactory scopes, ISystemSettingsCache settings, ILogger<UcumSynchronizationWorker> logger)
        => (_scopes, _settings, _logger) = (scopes, settings, logger);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (await _settings.GetBoolAsync("Terminology:Ucum:SchedulerEnabled", false, stoppingToken))
            {
                var now = DateTimeOffset.Now;
                var frequency = await _settings.GetStringAsync("Terminology:Ucum:Frequency", "Weekly", stoppingToken);
                var configured = await _settings.GetStringAsync("Terminology:Ucum:ExecutionTime", "05:00", stoppingToken);
                var dueTime = TimeOnly.TryParse(configured, out var time) ? time : new TimeOnly(5, 0);
                var due = now.TimeOfDay >= dueTime.ToTimeSpan() && _lastRunDate != DateOnly.FromDateTime(now.DateTime)
                    && (string.Equals(frequency, "Weekly", StringComparison.OrdinalIgnoreCase) ? now.DayOfWeek == DayOfWeek.Monday : now.Day == 1);
                if (due)
                {
                    try
                    {
                        using var scope = _scopes.CreateScope();
                        var result = await scope.ServiceProvider.GetRequiredService<IUcumSynchronizationService>().SynchronizeAsync(stoppingToken);
                        _lastRunDate = DateOnly.FromDateTime(now.DateTime);
                        _logger.LogInformation("UCUM synchronization completed: {Version}, {Count} units.", result.Version, result.ImportedConceptCount);
                    }
                    catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                    {
                        _logger.LogError(ex, "Scheduled UCUM synchronization failed.");
                    }
                }
            }
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }
}
