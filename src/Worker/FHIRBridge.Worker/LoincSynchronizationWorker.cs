using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Terminology;

namespace FHIRBridge.Worker;

/// <summary>Polls hourly and runs the shared, idempotent LOINC synchronization pipeline at its configured local time.</summary>
public sealed class LoincSynchronizationWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ISystemSettingsCache _settings;
    private readonly ILogger<LoincSynchronizationWorker> _logger;
    private DateOnly? _lastRunDate;
    public LoincSynchronizationWorker(IServiceScopeFactory scopes, ISystemSettingsCache settings, ILogger<LoincSynchronizationWorker> logger) => (_scopes, _settings, _logger) = (scopes, settings, logger);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (await _settings.GetBoolAsync("Terminology:Loinc:SchedulerEnabled", false, stoppingToken))
            {
                var now = DateTimeOffset.Now; var frequency = await _settings.GetStringAsync("Terminology:Loinc:Frequency", "Monthly", stoppingToken);
                var configured = await _settings.GetStringAsync("Terminology:Loinc:ExecutionTime", "02:00", stoppingToken);
                var dueTime = TimeOnly.TryParse(configured, out var time) ? time : new TimeOnly(2, 0);
                var due = now.TimeOfDay >= dueTime.ToTimeSpan() && _lastRunDate != DateOnly.FromDateTime(now.DateTime)
                    && (string.Equals(frequency, "Weekly", StringComparison.OrdinalIgnoreCase) || now.Day == 1);
                if (due)
                {
                    try { using var scope = _scopes.CreateScope(); var result = await scope.ServiceProvider.GetRequiredService<ILoincSynchronizationService>().SynchronizeAsync(stoppingToken); _lastRunDate = DateOnly.FromDateTime(now.DateTime); _logger.LogInformation("LOINC synchronization completed: {Version}, {Count} terms, already current: {Current}.", result.Version, result.ImportedConceptCount, result.AlreadyCurrent); }
                    catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { _logger.LogError(ex, "Scheduled LOINC synchronization failed."); }
                }
            }
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }
}
