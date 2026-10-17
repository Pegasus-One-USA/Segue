using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Application.Workflows.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Persistence.Workflows;

/// <summary>
/// Keeps workflow-run status true when several API and Worker instances share one database.
/// <list type="number">
/// <item><b>Heartbeat.</b> Every <see cref="WorkflowRunLease.HeartbeatInterval"/> this process renews the lease of each
/// run it is executing (<see cref="IActiveWorkflowRuns"/>) and confirms the new expiry there. A run whose lease could
/// not be renewed is fenced by <see cref="IActiveWorkflowRuns.HoldsLease"/> before it lapses, and a run another
/// instance already closed is stopped at once.</item>
/// <item><b>Cancel from another instance.</b> A run whose row says a user asked to cancel it is stopped here, through
/// <see cref="IWorkflowRunTracker"/>, between nodes, as a Cancel made on this instance would be.</item>
/// <item><b>Interrupted runs.</b> A Running run whose lease lapsed has no process left to finish it (restart, shutdown,
/// crash, or a save that failed while recording the outcome). It is failed with a reason Execution History shows,
/// never restarted. Any instance may do this: only a lease nobody renewed for <see cref="WorkflowRunLease.Duration"/>
/// lapses, so a run another instance is executing is never touched.</item>
/// </list>
/// Registered in both the API and the Worker. Replaces the start-up-only reconciler, which assumed one instance per
/// host and so could close a run another instance was still executing.
/// </summary>
public sealed class WorkflowRunLeaseService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IActiveWorkflowRuns _activeRuns;
    private readonly IWorkflowRunTracker _tracker;
    private readonly ILogger<WorkflowRunLeaseService> _logger;
    private readonly TimeProvider _time;

    public WorkflowRunLeaseService(
        IServiceScopeFactory scopeFactory,
        IActiveWorkflowRuns activeRuns,
        IWorkflowRunTracker tracker,
        ILogger<WorkflowRunLeaseService> logger,
        TimeProvider? time = null)
    {
        _scopeFactory = scopeFactory;
        _activeRuns = activeRuns;
        _tracker = tracker;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(WorkflowRunLease.HeartbeatInterval, _time);
        do
        {
            await TickAsync(stoppingToken);
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    /// <summary>One heartbeat: renew, pass on cancel requests, close lapsed runs. Never throws (but for shutdown):
    /// a failed tick is retried on the next one.</summary>
    public async Task TickAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var runStore = scope.ServiceProvider.GetRequiredService<IWorkflowRunStore>();
            var now = _time.GetUtcNow();

            var held = _activeRuns.Snapshot();
            if (held.Count > 0)
            {
                // The expiry is computed from the time before the write, so the confirmed lease is never later than
                // the one the database holds.
                var expiresAt = now + WorkflowRunLease.Duration;
                var renewal = await runStore.RenewLeasesAsync(held, _activeRuns.InstanceId, expiresAt, cancellationToken);
                _activeRuns.Confirm(renewal.Renewed, expiresAt);
                foreach (var runId in renewal.CancellationRequested)
                {
                    if (_tracker.RequestCancellation(runId))
                    {
                        _logger.LogInformation("Workflow run {WorkflowRunId} cancelled at the request of another instance.", runId);
                    }
                }

                // Closed elsewhere while this process still runs it (its lease lapsed there): stop before it does more.
                foreach (var runId in renewal.Closed)
                {
                    _activeRuns.Revoke(runId);
                    _tracker.RequestCancellation(runId);
                    _logger.LogWarning(
                        "Workflow run {WorkflowRunId} was closed by another instance while running here; stopping it.", runId);
                }
            }

            var interrupted = await runStore.FailExpiredRunsAsync(
                now, now - WorkflowRunLease.LegacyGrace, WorkflowRunLease.InterruptedReason, cancellationToken);
            if (interrupted > 0)
            {
                _logger.LogWarning(
                    "Marked {Count} workflow run(s) as failed: nothing renewed their lease, so the process running them stopped.",
                    interrupted);
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(exception, "Workflow run lease heartbeat failed; it is retried on the next tick.");
        }
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
