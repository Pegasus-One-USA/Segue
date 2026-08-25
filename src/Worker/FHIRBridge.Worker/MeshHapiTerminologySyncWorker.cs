using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Infrastructure.Terminology.Hapi;

namespace FHIRBridge.Worker;

/// <summary>
/// Polls hourly and, when due, automatically downloads the latest official NLM MeSH descriptor file
/// and loads it into the embedded HAPI terminology server. Mirrors
/// <see cref="HcpcsHapiTerminologySyncWorker"/> exactly. Disabled by default via
/// Terminology:MeshHapi:SchedulerEnabled.
/// </summary>
public sealed class MeshHapiTerminologySyncWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ISystemSettingsCache _settings;
    private readonly ILogger<MeshHapiTerminologySyncWorker> _logger;
    private DateOnly? _lastRunDate;

    public MeshHapiTerminologySyncWorker(
        IServiceScopeFactory scopes, ISystemSettingsCache settings, ILogger<MeshHapiTerminologySyncWorker> logger) =>
        (_scopes, _settings, _logger) = (scopes, settings, logger);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await _settings.GetBoolAsync("Terminology:MeshHapi:SchedulerEnabled", false, stoppingToken))
                {
                    var now = DateTimeOffset.Now;
                    var frequency = await _settings.GetStringAsync("Terminology:MeshHapi:Frequency", "Monthly", stoppingToken);
                    var configured = await _settings.GetStringAsync("Terminology:MeshHapi:ExecutionTime", "05:15", stoppingToken);
                    var dueTime = TimeOnly.TryParse(configured, out var time) ? time : new TimeOnly(5, 15);
                    var due = now.TimeOfDay >= dueTime.ToTimeSpan() && _lastRunDate != DateOnly.FromDateTime(now.DateTime)
                        && (string.Equals(frequency, "Weekly", StringComparison.OrdinalIgnoreCase) || now.Day == 1);

                    if (due)
                    {
                        using var scope = _scopes.CreateScope();
                        var result = await scope.ServiceProvider
                            .GetRequiredService<IHapiMeshTerminologySyncService>()
                            .SyncAsync(stoppingToken);
                        _lastRunDate = DateOnly.FromDateTime(now.DateTime);
                        _logger.LogInformation(
                            "MeSH HAPI terminology sync completed: {Total} codes, {Elapsed}.",
                            result.TotalConceptCount,
                            result.Duration);
                    }
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Scheduled MeSH HAPI terminology sync failed.");
            }

            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }
}
