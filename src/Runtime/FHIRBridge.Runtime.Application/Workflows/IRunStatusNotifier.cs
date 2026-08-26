namespace FHIRBridge.Runtime.Application.Workflows;

/// <summary>
/// Best-effort live push of a <see cref="FHIRBridge.Runtime.Domain.Workflows.WorkflowRun"/>'s status
/// transition, for real-time UI updates (Dashboard, Workflow List). Optional (constructor-injected as
/// nullable, same pattern as <c>IWorkflowAuditRecorder</c>/<c>IGlobalExceptionManager</c> on
/// <see cref="RankedWorkflowOrchestrator"/>) — a host that never registers an implementation (e.g. the
/// Worker process, which has no SignalR hub of its own to push into) simply resolves this as null, and
/// those runs still persist normally; only the live push is skipped, not the run itself.
/// </summary>
public interface IRunStatusNotifier
{
    Task NotifyAsync(RunStatusChangedEvent statusEvent, CancellationToken cancellationToken);
}

/// <summary>Status: "Running" | "Succeeded" | "Failed" — mirrors <c>WorkflowRunStatus</c> as a string so this
/// type has no dependency on the Domain project beyond the ids it already carries.</summary>
/// <param name="ErrorReferenceId">The Global Exception Manager's <c>ERR-yyyyMMdd-NNNNNN</c> id for this
/// failure/cancellation, when one was actually persisted to ErrorLogs — null whenever no capture ran or the
/// capture itself failed to persist (never a placeholder; see <c>ErrorReport.ErrorReferenceId</c>).</param>
public sealed record RunStatusChangedEvent(
    Guid WorkflowRunId,
    Guid WorkflowDefinitionId,
    string Status,
    DateTimeOffset OccurredAt,
    string? ErrorMessage = null,
    string? ErrorReferenceId = null);
