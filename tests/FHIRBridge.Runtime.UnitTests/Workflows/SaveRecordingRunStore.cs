using FHIRBridge.Runtime.Application.Workflows.Storage;
using FHIRBridge.Runtime.Domain.Workflows;

namespace FHIRBridge.Runtime.UnitTests.Workflows;

/// <summary>The in-memory store keeps the live <see cref="WorkflowRun"/> reference, so reading it back shows every
/// in-place mutation whether or not it was saved. This records the status each SaveAsync actually wrote.</summary>
internal sealed class SaveRecordingRunStore : IWorkflowRunStore
{
    private readonly InMemoryWorkflowRunStore _inner = new();
    private readonly Dictionary<Guid, WorkflowRunStatus> _lastSavedStatus = [];

    public WorkflowRunStatus? LastSavedStatus(Guid workflowRunId)
        => _lastSavedStatus.TryGetValue(workflowRunId, out var status) ? status : null;

    /// <summary>When set, every save throws, as a store whose database is unreachable would.</summary>
    public bool FailSaves { get; set; }

    public Task SaveAsync(WorkflowRun workflowRun, CancellationToken cancellationToken)
    {
        if (FailSaves)
        {
            throw new InvalidOperationException("The run store is unavailable.");
        }

        _lastSavedStatus[workflowRun.Id] = workflowRun.Status;
        return _inner.SaveAsync(workflowRun, cancellationToken);
    }

    public Task<WorkflowRun?> GetAsync(Guid workflowRunId, CancellationToken cancellationToken)
        => _inner.GetAsync(workflowRunId, cancellationToken);

    public Task<int> ExpireStaleValidatedAsync(DateTimeOffset olderThanUtc, CancellationToken cancellationToken)
        => _inner.ExpireStaleValidatedAsync(olderThanUtc, cancellationToken);

    public Task<int> FailInterruptedRunsAsync(
        WorkflowRunHost host, DateTimeOffset startedBeforeUtc, string reason, CancellationToken cancellationToken)
        => _inner.FailInterruptedRunsAsync(host, startedBeforeUtc, reason, cancellationToken);

    public Task<WorkflowRun?> FindValidatedAsync(Guid workflowDefinitionId, string correlationId, CancellationToken cancellationToken)
        => _inner.FindValidatedAsync(workflowDefinitionId, correlationId, cancellationToken);

    public Task<IReadOnlyCollection<WorkflowRun>> ListByDefinitionAsync(Guid workflowDefinitionId, CancellationToken cancellationToken)
        => _inner.ListByDefinitionAsync(workflowDefinitionId, cancellationToken);

    public Task<IReadOnlyCollection<WorkflowRun>> ListRecentAsync(int count, CancellationToken cancellationToken)
        => _inner.ListRecentAsync(count, cancellationToken);

    public Task<IReadOnlyDictionary<WorkflowRunStatus, int>> GetStatusCountsAsync(CancellationToken cancellationToken)
        => _inner.GetStatusCountsAsync(cancellationToken);
}
