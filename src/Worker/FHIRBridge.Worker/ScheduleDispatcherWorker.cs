using FHIRBridge.Application.Abstractions.Scheduling;
using Microsoft.Extensions.Options;

namespace FHIRBridge.Worker;

/// <summary>
/// Dispatcher worker role: on a timer, claims due scheduled runs and enqueues pipeline-run commands to the messaging
/// transport. The actual pipeline execution is performed by the processor role (<see cref="PipelineRunCommandProcessor"/>),
/// which consumes the queue.
/// </summary>
/// <remarks>
/// <b>Live scheduler (2026-07-18 migration).</b> Registered in <c>Program.cs</c> and enabled by default via
/// <see cref="ScheduleDispatcherOptions.Enabled"/>. <c>Worker.RunDueRoutesAsync</c> (the previous direct-call
/// scheduler) is now off by default — see <c>RuntimeWorkerOptions.DirectRouteSchedulingEnabled</c> — and the Worker
/// host fails fast at startup if both are ever enabled simultaneously. See the remarks on
/// <c>FHIRBridge.Infrastructure.Scheduling.ScheduleDispatcher</c> for why that combination is unsafe, and
/// docs/backend/08-governance-logging-status.md for the full history.
/// </remarks>
public sealed class ScheduleDispatcherWorker : BackgroundService
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IOptions<ScheduleDispatcherOptions> _options;
    private readonly ILogger<ScheduleDispatcherWorker> _logger;

    public ScheduleDispatcherWorker(
        IServiceScopeFactory serviceScopeFactory,
        IOptions<ScheduleDispatcherOptions> options,
        ILogger<ScheduleDispatcherWorker> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Value.Enabled)
        {
            _logger.LogInformation(
                "Schedule dispatcher is disabled. Set ScheduleDispatcher:Enabled=true to enqueue due scheduled runs.");
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(15, _options.Value.IntervalSeconds)));

        do
        {
            await DispatchAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task DispatchAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceScopeFactory.CreateScope();

        try
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<IScheduleDispatcher>();
            await dispatcher.DispatchDueRunsAsync(DateTime.UtcNow, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Schedule dispatcher tick failed.");
        }
    }
}
