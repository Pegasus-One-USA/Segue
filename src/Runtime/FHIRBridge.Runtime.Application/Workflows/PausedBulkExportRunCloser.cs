using FHIRBridge.Governance;
using FHIRBridge.Observability.Logging;
using FHIRBridge.Runtime.Application.Workflows.Storage;
using FHIRBridge.Runtime.Domain.Workflows;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Runtime.Application.Workflows;

/// <summary>
/// Closes a workflow run paused on a bulk <c>$export</c> whose job will never resume it. A paused run is only ever
/// resumed by its job completing (<see cref="IRankedWorkflowOrchestrator.ResumeAfterBulkExportAsync"/>); when the job
/// fails or is cancelled instead, nothing closed the run, so it stayed <c>AwaitingBulkExport</c> — "Running" on
/// Execution History and in the Bulk Data Status popup — for ever, with no status event to update either.
/// </summary>
public interface IPausedBulkExportRunCloser
{
    /// <summary>Closes the run as <c>Failed</c>. False, and nothing written or announced, when the run is not
    /// <c>AwaitingBulkExport</c>: a run that already finished is never overwritten.</summary>
    Task<bool> FailAsync(Guid workflowRunId, string reason, CancellationToken cancellationToken);

    /// <summary>Same as <see cref="FailAsync"/>, closing the run as <c>Cancelled</c>.</summary>
    Task<bool> CancelAsync(Guid workflowRunId, string reason, CancellationToken cancellationToken);
}

/// <summary>
/// Kept apart from <see cref="RankedWorkflowOrchestrator"/>: closing a run needs only the run store, not the node
/// executors, so callers such as the bulk-export cancel endpoint don't resolve the whole execution graph.
/// <para>The outcome is saved first, and a save failure propagates so the caller sees it. It is announced only once
/// saved, through the same <see cref="IRunStatusNotifier"/> the orchestrator uses, and a failed announcement never
/// touches the saved outcome.</para>
/// </summary>
public sealed class PausedBulkExportRunCloser : IPausedBulkExportRunCloser
{
    private readonly IWorkflowRunStore _runStore;
    private readonly ILogger<PausedBulkExportRunCloser> _logger;
    private readonly IRunStatusNotifier? _runStatusNotifier;
    private readonly IGlobalExceptionManager? _exceptionManager;

    public PausedBulkExportRunCloser(
        IWorkflowRunStore runStore,
        ILogger<PausedBulkExportRunCloser> logger,
        IRunStatusNotifier? runStatusNotifier = null,
        IGlobalExceptionManager? exceptionManager = null)
    {
        _runStore = runStore;
        _logger = logger;
        _runStatusNotifier = runStatusNotifier;
        _exceptionManager = exceptionManager;
    }

    public Task<bool> FailAsync(Guid workflowRunId, string reason, CancellationToken cancellationToken)
        => CloseAsync(workflowRunId, reason, cancel: false, cancellationToken);

    public Task<bool> CancelAsync(Guid workflowRunId, string reason, CancellationToken cancellationToken)
        => CloseAsync(workflowRunId, reason, cancel: true, cancellationToken);

    private async Task<bool> CloseAsync(Guid workflowRunId, string reason, bool cancel, CancellationToken cancellationToken)
    {
        var workflowRun = await _runStore.GetAsync(workflowRunId, cancellationToken);
        if (workflowRun is not { Status: WorkflowRunStatus.AwaitingBulkExport })
        {
            return false;
        }

        if (_exceptionManager is not null)
        {
            // Same Errors-section entry the orchestrator records for its own failed/cancelled runs, so the run's
            // reference id resolves in Operations → Errors.
            var referenceId = await _exceptionManager.CaptureExpectedAsync(
                new ExpectedFailure(cancel ? "BulkExportCancelled" : "BulkExportFailed", reason),
                new ExceptionContext(
                    Module: "Workflow",
                    Severity: cancel ? "Informational" : "Error",
                    CorrelationId: workflowRun.CorrelationId,
                    WorkflowId: workflowRun.WorkflowDefinitionId.ToString(),
                    ExecutionId: workflowRun.Id.ToString()),
                CancellationToken.None);
            workflowRun.SetErrorReference(referenceId);
        }

        var closedAt = DateTimeOffset.UtcNow;
        if (cancel)
        {
            workflowRun.Cancel(reason, closedAt);
        }
        else
        {
            workflowRun.Fail(reason, closedAt);
        }

        await _runStore.SaveAsync(workflowRun, cancellationToken);

        _logger.Log(
            cancel ? LogLevel.Warning : LogLevel.Error,
            cancel ? LogEvents.WorkflowRunCancelled : LogEvents.WorkflowRunFailed,
            "Workflow run {WorkflowRunId} closed as {Status} while awaiting its bulk export: {Reason}",
            workflowRun.Id, workflowRun.Status, reason);

        await NotifyAsync(workflowRun, closedAt, reason);
        return true;
    }

    private async Task NotifyAsync(WorkflowRun workflowRun, DateTimeOffset occurredAt, string reason)
    {
        if (_runStatusNotifier is null)
        {
            return;
        }

        try
        {
            await _runStatusNotifier.NotifyAsync(
                new RunStatusChangedEvent(
                    workflowRun.Id, workflowRun.WorkflowDefinitionId, workflowRun.Status.ToString(), occurredAt,
                    reason, workflowRun.ErrorReferenceId),
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            // Swallowed by design, as in RankedWorkflowOrchestrator.NotifyRunStatusAsync: the outcome is already saved
            // and shows on the next fetch; only the live update is lost.
            SwallowedError.Report(exception, "Workflow.Run status notification");
        }
    }
}
