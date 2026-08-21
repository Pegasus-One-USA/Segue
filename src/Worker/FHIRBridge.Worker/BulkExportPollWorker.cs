using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Services;
using FHIRBridge.Governance;
using Microsoft.Extensions.Options;

namespace FHIRBridge.Worker;

/// <summary>
/// Poller worker role: on a timer, checks the status of every in-flight <c>BulkExportJob</c> (one GET per job per
/// tick, via <see cref="IBulkExportPollService"/>) instead of any caller blocking inline for a bulk-export job's
/// full duration (which can run to ~2 hours). Modeled directly on <see cref="ScheduleDispatcherWorker"/>.
/// </summary>
public sealed class BulkExportPollWorker : BackgroundService
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IOptions<BulkExportPollOptions> _options;
    private readonly ISystemSettingsCache _settingsCache;
    private readonly ILogger<BulkExportPollWorker> _logger;

    public BulkExportPollWorker(
        IServiceScopeFactory serviceScopeFactory,
        IOptions<BulkExportPollOptions> options,
        ISystemSettingsCache settingsCache,
        ILogger<BulkExportPollWorker> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _options = options;
        _settingsCache = settingsCache;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var enabled = await _settingsCache.GetBoolAsync(
                "BulkExportPoll:Enabled", _options.Value.Enabled, stoppingToken);
            if (!enabled)
            {
                _logger.LogInformation(
                    "Bulk export poller is disabled. Set BulkExportPoll:Enabled=true to resume in-flight $export jobs.");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                continue;
            }

            await PollAsync(stoppingToken);

            var intervalSeconds = await _settingsCache.GetIntAsync(
                "BulkExportPoll:IntervalSeconds", _options.Value.IntervalSeconds, stoppingToken);
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(15, intervalSeconds)), stoppingToken);
        }
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceScopeFactory.CreateScope();

        try
        {
            var pollService = scope.ServiceProvider.GetRequiredService<IBulkExportPollService>();
            await pollService.PollDueJobsAsync(
                _options.Value.MaxBatchSize, _options.Value.DefaultPollIntervalSeconds, _options.Value.MaxPollAttempts, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Bulk export poll tick failed.");

            var exceptionManager = scope.ServiceProvider.GetRequiredService<IGlobalExceptionManager>();
            await exceptionManager.CaptureAsync(
                exception,
                new ExceptionContext(Module: "Bulk Export Poll"),
                CancellationToken.None);
        }
    }
}
