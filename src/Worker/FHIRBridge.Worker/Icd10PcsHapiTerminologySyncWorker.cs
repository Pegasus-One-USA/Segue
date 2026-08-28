using FHIRBridge.Application.Services;
using FHIRBridge.Application.Services.Terminology;
using FHIRBridge.Infrastructure.Terminology.Hapi;

namespace FHIRBridge.Worker;

/// <summary>
/// Polls hourly and, when due, automatically downloads the latest official CMS ICD-10-PCS order
/// file and loads it into the embedded HAPI terminology server. Mirrors
/// <see cref="HcpcsHapiTerminologySyncWorker"/> exactly (same "find the latest dated zip" pattern).
/// Disabled by default via Terminology:Icd10PcsHapi:SchedulerEnabled.
/// </summary>
public sealed class Icd10PcsHapiTerminologySyncWorker : BackgroundService
{
    private static readonly TerminologySyncScheduleConfig ScheduleConfig = new("Terminology:Icd10PcsHapi", "05:00");

    private readonly IServiceScopeFactory _scopes;
    private readonly ITerminologySyncScheduleEvaluator _scheduleEvaluator;
    private readonly ILogger<Icd10PcsHapiTerminologySyncWorker> _logger;

    public Icd10PcsHapiTerminologySyncWorker(
        IServiceScopeFactory scopes,
        ITerminologySyncScheduleEvaluator scheduleEvaluator,
        ILogger<Icd10PcsHapiTerminologySyncWorker> logger) =>
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
                        .GetRequiredService<IHapiIcd10PcsTerminologySyncService>()
                        .SyncAsync(stoppingToken);
                    await scope.ServiceProvider.GetRequiredService<ISystemSettingsService>()
                        .SetAsync("Terminology:Icd10PcsHapi:LastRunUtc", DateTime.UtcNow.ToString("O"), null, stoppingToken);
                    _logger.LogInformation(
                        "ICD-10-PCS HAPI terminology sync completed: {Total} codes ({Billable} billable), {Elapsed}.",
                        result.TotalConceptCount,
                        result.BillableConceptCount,
                        result.Duration);
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Scheduled ICD-10-PCS HAPI terminology sync failed.");
            }

            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }
}
