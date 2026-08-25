using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Infrastructure.Terminology.Hapi;

namespace FHIRBridge.Worker;

/// <summary>
/// Polls hourly and, when due, automatically downloads the latest official CMS HCPCS Level II
/// quarterly release and loads it into the embedded HAPI terminology server. Mirrors
/// <see cref="CvxHapiTerminologySyncWorker"/> exactly. Disabled by default via
/// Terminology:HcpcsHapi:SchedulerEnabled.
/// </summary>
public sealed class HcpcsHapiTerminologySyncWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ISystemSettingsCache _settings;
    private readonly ILogger<HcpcsHapiTerminologySyncWorker> _logger;
    private DateOnly? _lastRunDate;

    public HcpcsHapiTerminologySyncWorker(
        IServiceScopeFactory scopes, ISystemSettingsCache settings, ILogger<HcpcsHapiTerminologySyncWorker> logger) =>
        (_scopes, _settings, _logger) = (scopes, settings, logger);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await _settings.GetBoolAsync("Terminology:HcpcsHapi:SchedulerEnabled", false, stoppingToken))
                {
                    var now = DateTimeOffset.Now;
                    var frequency = await _settings.GetStringAsync("Terminology:HcpcsHapi:Frequency", "Monthly", stoppingToken);
                    var configured = await _settings.GetStringAsync("Terminology:HcpcsHapi:ExecutionTime", "03:45", stoppingToken);
                    var dueTime = TimeOnly.TryParse(configured, out var time) ? time : new TimeOnly(3, 45);
                    var due = now.TimeOfDay >= dueTime.ToTimeSpan() && _lastRunDate != DateOnly.FromDateTime(now.DateTime)
                        && (string.Equals(frequency, "Weekly", StringComparison.OrdinalIgnoreCase) || now.Day == 1);

                    if (due)
                    {
                        using var scope = _scopes.CreateScope();
                        var result = await scope.ServiceProvider
                            .GetRequiredService<IHapiHcpcsTerminologySyncService>()
                            .SyncAsync(stoppingToken);
                        _lastRunDate = DateOnly.FromDateTime(now.DateTime);
                        _logger.LogInformation(
                            "HCPCS HAPI terminology sync completed: {Total} codes, {Elapsed}.",
                            result.TotalConceptCount,
                            result.Duration);
                    }
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Scheduled HCPCS HAPI terminology sync failed.");
            }

            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }
}
