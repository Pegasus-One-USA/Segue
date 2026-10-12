namespace FHIRBridge.Runtime.Application.Workflows.Storage;

/// <summary>What one heartbeat found for the runs this process holds (see
/// <see cref="IWorkflowRunStore.RenewLeasesAsync"/>).</summary>
/// <param name="Renewed">Still Running; the lease now runs to the requested expiry.</param>
/// <param name="CancellationRequested">Renewed, and a user asked to cancel from another instance.</param>
/// <param name="Closed">Already finished, failed or cancelled on record: another instance closed it (its lease
/// lapsed there), so this process must stop working on it.</param>
public sealed record WorkflowLeaseRenewal(
    IReadOnlyCollection<Guid> Renewed,
    IReadOnlyCollection<Guid> CancellationRequested,
    IReadOnlyCollection<Guid> Closed)
{
    public static readonly WorkflowLeaseRenewal None = new([], [], []);
}

/// <summary>A run's outcome was not saved because its row was already closed by someone else (another instance
/// failed it as interrupted, or it was cancelled as abandoned). The recorded outcome stands.</summary>
public sealed class WorkflowRunAlreadyClosedException : InvalidOperationException
{
    public WorkflowRunAlreadyClosedException(Guid workflowRunId, string recordedStatus)
        : base($"Workflow run {workflowRunId} was already closed as {recordedStatus} by another instance; " +
               "its outcome here was not saved over that.")
    {
        WorkflowRunId = workflowRunId;
        RecordedStatus = recordedStatus;
    }

    public Guid WorkflowRunId { get; }

    public string RecordedStatus { get; }
}
