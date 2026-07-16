using FHIRBridge.Application.Abstractions.Audit;
using FHIRBridge.Application.Abstractions.Scheduling;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using Microsoft.Extensions.Options;

namespace FHIRBridge.Worker;

/// <summary>
/// Dispatcher worker role: on a timer, claims due scheduled runs and enqueues pipeline-run commands to the messaging
/// transport. Disabled by default. The actual pipeline execution is performed by the processor role (Phase 2),
/// which consumes the queue.
/// </summary>
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
        var activityAuditService = scope.ServiceProvider.GetRequiredService<IUserActivityAuditService>();
        var currentUserService = scope.ServiceProvider.GetRequiredService<ICurrentUserService>();

        try
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<IScheduleDispatcher>();
            var enqueuedCount = await dispatcher.DispatchDueRunsAsync(DateTime.UtcNow, cancellationToken);

            // Only record an Activity Feed entry when something was actually due — a tick that found nothing to
            // enqueue (the common case, every 15s+ by default) would otherwise flood the feed with noise.
            if (enqueuedCount > 0)
            {
                await RecordActivityAsync(
                    activityAuditService, currentUserService,
                    $"Scheduler started — {enqueuedCount} due run(s) enqueued", UserActivityStatuses.Success, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Schedule dispatcher tick failed.");
            await RecordActivityAsync(
                activityAuditService, currentUserService,
                $"Scheduler failed: {exception.Message}", UserActivityStatuses.Failed, cancellationToken);
        }
    }

    private static Task RecordActivityAsync(
        IUserActivityAuditService activityAuditService,
        ICurrentUserService currentUserService,
        string activity,
        string status,
        CancellationToken cancellationToken)
    {
        var user = currentUserService.CurrentUser;
        return activityAuditService.RecordAsync(
            new RecordUserActivityRequest(
                UserId: null,
                UserEmail: user.AuditName,
                Category: UserActivityCategories.Administration,
                Activity: activity,
                Status: status,
                EntityName: "Scheduler"),
            cancellationToken);
    }
}
