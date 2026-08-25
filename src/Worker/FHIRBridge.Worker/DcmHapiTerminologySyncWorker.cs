using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Infrastructure.Terminology.Hapi;

namespace FHIRBridge.Worker;

/// <summary>
/// Polls hourly and, when due, automatically downloads the official DICOM Controlled Terminology
/// (DCM) ontology and loads it into the embedded HAPI terminology server. Mirrors
/// <see cref="UcumHapiTerminologySyncWorker"/> exactly. Disabled by default via
/// Terminology:DcmHapi:SchedulerEnabled.
/// </summary>
public sealed class DcmHapiTerminologySyncWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ISystemSettingsCache _settings;
    private readonly ILogger<DcmHapiTerminologySyncWorker> _logger;
    private DateOnly? _lastRunDate;

    public DcmHapiTerminologySyncWorker(
        IServiceScopeFactory scopes, ISystemSettingsCache settings, ILogger<DcmHapiTerminologySyncWorker> logger) =>
        (_scopes, _settings, _logger) = (scopes, settings, logger);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await _settings.GetBoolAsync("Terminology:DcmHapi:SchedulerEnabled", false, stoppingToken))
                {
                    var now = DateTimeOffset.Now;
                    var frequency = await _settings.GetStringAsync("Terminology:DcmHapi:Frequency", "Monthly", stoppingToken);
                    var configured = await _settings.GetStringAsync("Terminology:DcmHapi:ExecutionTime", "05:30", stoppingToken);
                    var dueTime = TimeOnly.TryParse(configured, out var time) ? time : new TimeOnly(5, 30);
                    var due = now.TimeOfDay >= dueTime.ToTimeSpan() && _lastRunDate != DateOnly.FromDateTime(now.DateTime)
                        && (string.Equals(frequency, "Weekly", StringComparison.OrdinalIgnoreCase) || now.Day == 1);

                    if (due)
                    {
                        using var scope = _scopes.CreateScope();
                        var result = await scope.ServiceProvider
                            .GetRequiredService<IHapiDcmTerminologySyncService>()
                            .SyncAsync(stoppingToken);
                        _lastRunDate = DateOnly.FromDateTime(now.DateTime);
                        _logger.LogInformation(
                            "DCM HAPI terminology sync completed: {Total} codes, {Elapsed}.",
                            result.TotalConceptCount,
                            result.Duration);
                    }
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Scheduled DCM HAPI terminology sync failed.");
            }

            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }
}
