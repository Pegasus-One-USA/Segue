using FHIRBridge.Application.Abstractions.Governance;
using Microsoft.Extensions.Options;

namespace FHIRBridge.Worker;

/// <summary>
/// Periodically runs the retention purge, removing expired records from purgeable stores per the retention policy.
/// Disabled by default; enable via RetentionPurge:Enabled with an IntervalHours. The HIPAA audit log is immutable and
/// is never purged.
/// </summary>
public sealed class RetentionPurgeWorker : BackgroundService
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IOptions<RetentionPurgeOptions> _options;
    private readonly ILogger<RetentionPurgeWorker> _logger;

    public RetentionPurgeWorker(
        IServiceScopeFactory serviceScopeFactory,
        IOptions<RetentionPurgeOptions> options,
        ILogger<RetentionPurgeWorker> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Value.Enabled)
        {
            _logger.LogInformation("Retention purge worker is disabled. Set RetentionPurge:Enabled=true to run it.");
            return;
        }

        var interval = TimeSpan.FromHours(Math.Max(1, _options.Value.IntervalHours));
        using var timer = new PeriodicTimer(interval);

        do
        {
            await PurgeAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task PurgeAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var purgeService = scope.ServiceProvider.GetRequiredService<IRetentionPurgeService>();
            var report = await purgeService.RunAsync(DateTime.UtcNow, cancellationToken);

            _logger.LogInformation(
                "Retention purge complete: removed {Total} record(s) older than {Cutoff:o} ({Years}y).",
                report.TotalPurged, report.CutoffUtc, report.RetentionYears);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Retention purge run failed.");
        }
    }
}

public sealed class RetentionPurgeOptions
{
    public bool Enabled { get; set; }
    public int IntervalHours { get; set; } = 24;
}
