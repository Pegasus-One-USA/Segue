using FHIRBridge.Runtime.Domain.Workflows;

namespace FHIRBridge.Runtime.Application.Workflows.Storage;

/// <summary>
/// Durable store for executed <see cref="WorkflowRun"/> aggregates (run + per-node runs + lineage).
/// The orchestrator persists a run once it reaches a terminal state, giving a node-by-node execution
/// history that survives a restart and can be replayed in the pipeline-builder timeline.
/// </summary>
public interface IWorkflowRunStore
{
    Task SaveAsync(WorkflowRun workflowRun, CancellationToken cancellationToken);

    Task<WorkflowRun?> GetAsync(Guid workflowRunId, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<WorkflowRun>> ListByDefinitionAsync(
        Guid workflowDefinitionId,
        CancellationToken cancellationToken);

    /// <summary>Most recent runs across every workflow definition, newest first — backs the global Execution History list.</summary>
    Task<IReadOnlyCollection<WorkflowRun>> ListRecentAsync(
        int count,
        CancellationToken cancellationToken);
}
