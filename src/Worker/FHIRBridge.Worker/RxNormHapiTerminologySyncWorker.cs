using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Infrastructure.Terminology.Hapi;

namespace FHIRBridge.Worker;

/// <summary>
/// Polls hourly and, when due, automatically downloads the credentialed RxNorm release (via the
/// existing UMLS/UTS API key) and loads it into the embedded HAPI terminology server. Mirrors
/// <see cref="LoincHapiTerminologySyncWorker"/> exactly. Disabled by default via
/// Terminology:RxNormHapi:SchedulerEnabled.
/// </summary>
public sealed class RxNormHapiTerminologySyncWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ISystemSettingsCache _settings;
    private readonly ILogger<RxNormHapiTerminologySyncWorker> _logger;
    private DateOnly? _lastRunDate;

    public RxNormHapiTerminologySyncWorker(
        IServiceScopeFactory scopes, ISystemSettingsCache settings, ILogger<RxNormHapiTerminologySyncWorker> logger) =>
        (_scopes, _settings, _logger) = (scopes, settings, logger);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await _settings.GetBoolAsync("Terminology:RxNormHapi:SchedulerEnabled", false, stoppingToken))
                {
                    var now = DateTimeOffset.Now;
                    var frequency = await _settings.GetStringAsync("Terminology:RxNormHapi:Frequency", "Monthly", stoppingToken);
                    var configured = await _settings.GetStringAsync("Terminology:RxNormHapi:ExecutionTime", "04:30", stoppingToken);
                    var dueTime = TimeOnly.TryParse(configured, out var time) ? time : new TimeOnly(4, 30);
                    var due = now.TimeOfDay >= dueTime.ToTimeSpan() && _lastRunDate != DateOnly.FromDateTime(now.DateTime)
                        && (string.Equals(frequency, "Weekly", StringComparison.OrdinalIgnoreCase) || now.Day == 1);

                    if (due)
                    {
                        using var scope = _scopes.CreateScope();
                        var result = await scope.ServiceProvider
                            .GetRequiredService<IHapiRxNormTerminologySyncService>()
                            .SyncAsync(stoppingToken);
                        _lastRunDate = DateOnly.FromDateTime(now.DateTime);
                        _logger.LogInformation(
                            "RxNorm HAPI terminology sync completed: {Version}, {Total} codes, {Elapsed}.",
                            result.Version,
                            result.TotalConceptCount,
                            result.Duration);
                    }
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Scheduled RxNorm HAPI terminology sync failed.");
            }

            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }
}
