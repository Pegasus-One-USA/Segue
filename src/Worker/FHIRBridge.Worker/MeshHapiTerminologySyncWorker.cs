using FHIRBridge.Application.Services;
using FHIRBridge.Application.Services.Terminology;
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
    private static readonly TerminologySyncScheduleConfig ScheduleConfig = new("Terminology:MeshHapi", "05:15");

    private readonly IServiceScopeFactory _scopes;
    private readonly ITerminologySyncScheduleEvaluator _scheduleEvaluator;
    private readonly ILogger<MeshHapiTerminologySyncWorker> _logger;

    public MeshHapiTerminologySyncWorker(
        IServiceScopeFactory scopes,
        ITerminologySyncScheduleEvaluator scheduleEvaluator,
        ILogger<MeshHapiTerminologySyncWorker> logger) =>
        (_scopes, _scheduleEvaluator, _logger) = (scopes, scheduleEvaluator, logger);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await _scheduleEvaluator.IsDueAsync(ScheduleConfig, DateTimeOffset.Now, stoppingToken))
                {
                    using var scope = _scopes.CreateScope();
                    var result = await scope.ServiceProvider
                        .GetRequiredService<IHapiMeshTerminologySyncService>()
                        .SyncAsync(stoppingToken);
                    await scope.ServiceProvider.GetRequiredService<ISystemSettingsService>()
                        .SetAsync("Terminology:MeshHapi:LastRunUtc", DateTime.UtcNow.ToString("O"), null, stoppingToken);
                    _logger.LogInformation(
                        "MeSH HAPI terminology sync completed: {Total} codes, {Elapsed}.",
                        result.TotalConceptCount,
                        result.Duration);
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
