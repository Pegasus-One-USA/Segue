using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Infrastructure.Terminology.Hapi;

namespace FHIRBridge.Worker;

/// <summary>
/// Polls hourly and, when due, automatically downloads the credentialed LOINC release (via the
/// existing LOINC account) and loads it into the embedded HAPI terminology server. Mirrors
/// <see cref="CvxHapiTerminologySyncWorker"/> exactly. Disabled by default via
/// Terminology:LoincHapi:SchedulerEnabled — and will fail (logged, not thrown) until a real LOINC
/// username/password are provisioned as ProvisionedSecrets, same as the existing LOINC sync path.
/// </summary>
public sealed class LoincHapiTerminologySyncWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ISystemSettingsCache _settings;
    private readonly ILogger<LoincHapiTerminologySyncWorker> _logger;
    private DateOnly? _lastRunDate;

    public LoincHapiTerminologySyncWorker(
        IServiceScopeFactory scopes, ISystemSettingsCache settings, ILogger<LoincHapiTerminologySyncWorker> logger) =>
        (_scopes, _settings, _logger) = (scopes, settings, logger);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await _settings.GetBoolAsync("Terminology:LoincHapi:SchedulerEnabled", false, stoppingToken))
                {
                    var now = DateTimeOffset.Now;
                    var frequency = await _settings.GetStringAsync("Terminology:LoincHapi:Frequency", "Monthly", stoppingToken);
                    var configured = await _settings.GetStringAsync("Terminology:LoincHapi:ExecutionTime", "04:15", stoppingToken);
                    var dueTime = TimeOnly.TryParse(configured, out var time) ? time : new TimeOnly(4, 15);
                    var due = now.TimeOfDay >= dueTime.ToTimeSpan() && _lastRunDate != DateOnly.FromDateTime(now.DateTime)
                        && (string.Equals(frequency, "Weekly", StringComparison.OrdinalIgnoreCase) || now.Day == 1);

                    if (due)
                    {
                        using var scope = _scopes.CreateScope();
                        var result = await scope.ServiceProvider
                            .GetRequiredService<IHapiLoincTerminologySyncService>()
                            .SyncAsync(stoppingToken);
                        _lastRunDate = DateOnly.FromDateTime(now.DateTime);
                        _logger.LogInformation(
                            "LOINC HAPI terminology sync completed: {Version}, {Total} codes, {Elapsed}.",
                            result.Version,
                            result.TotalConceptCount,
                            result.Duration);
                    }
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Scheduled LOINC HAPI terminology sync failed.");
            }

            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }
}
