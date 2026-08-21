using FHIRBridge.Runtime.Domain.Workflows;

namespace FHIRBridge.Runtime.Application.Workflows.Storage;

/// <summary>
/// Default, non-durable <see cref="IWorkflowRunStore"/>. Keeps the run history in process so the engine
/// works standalone (tests, single-instance dev); the SQL store overrides this in the composing host.
/// </summary>
public sealed class InMemoryWorkflowRunStore : IWorkflowRunStore
{
    private readonly Dictionary<Guid, WorkflowRun> _runs = [];

    public Task SaveAsync(WorkflowRun workflowRun, CancellationToken cancellationToken)
    {
        _runs[workflowRun.Id] = workflowRun;
        return Task.CompletedTask;
    }

    public Task<WorkflowRun?> GetAsync(Guid workflowRunId, CancellationToken cancellationToken)
    {
        _runs.TryGetValue(workflowRunId, out var workflowRun);
        return Task.FromResult(workflowRun);
    }

    public Task<IReadOnlyCollection<WorkflowRun>> ListByDefinitionAsync(
        Guid workflowDefinitionId,
        CancellationToken cancellationToken)
    {
        IReadOnlyCollection<WorkflowRun> runs = _runs.Values
            .Where(run => run.WorkflowDefinitionId == workflowDefinitionId)
            .OrderByDescending(run => run.StartedAt)
            .ToArray();

        return Task.FromResult(runs);
    }

    public Task<IReadOnlyCollection<WorkflowRun>> ListRecentAsync(
        int count,
        CancellationToken cancellationToken)
    {
        IReadOnlyCollection<WorkflowRun> runs = _runs.Values
            .OrderByDescending(run => run.StartedAt)
            .Take(Math.Clamp(count, 1, 1000))
            .ToArray();

        return Task.FromResult(runs);
    }

    public Task<IReadOnlyDictionary<WorkflowRunStatus, int>> GetStatusCountsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<WorkflowRunStatus, int> counts = Enum.GetValues<WorkflowRunStatus>()
            .ToDictionary(status => status, status => _runs.Values.Count(run => run.Status == status));

        return Task.FromResult(counts);
    }
}
