using FHIRBridge.Application.Services;
using FHIRBridge.Application.Services.Terminology;
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
    private static readonly TerminologySyncScheduleConfig ScheduleConfig = new("Terminology:LoincHapi", "04:15");

    private readonly IServiceScopeFactory _scopes;
    private readonly ITerminologySyncScheduleEvaluator _scheduleEvaluator;
    private readonly ILogger<LoincHapiTerminologySyncWorker> _logger;

    public LoincHapiTerminologySyncWorker(
        IServiceScopeFactory scopes,
        ITerminologySyncScheduleEvaluator scheduleEvaluator,
        ILogger<LoincHapiTerminologySyncWorker> logger) =>
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
                        .GetRequiredService<IHapiLoincTerminologySyncService>()
                        .SyncAsync(stoppingToken);
                    await scope.ServiceProvider.GetRequiredService<ISystemSettingsService>()
                        .SetAsync("Terminology:LoincHapi:LastRunUtc", DateTime.UtcNow.ToString("O"), null, stoppingToken);
                    _logger.LogInformation(
                        "LOINC HAPI terminology sync completed: {Version}, {Total} codes, {Elapsed}.",
                        result.Version,
                        result.TotalConceptCount,
                        result.Duration);
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
