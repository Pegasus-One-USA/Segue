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

    public Task<int> ExpireStaleValidatedAsync(DateTimeOffset olderThanUtc, CancellationToken cancellationToken)
    {
        var stale = _runs.Values
            .Where(run => run.Status == WorkflowRunStatus.Validated && run.StartedAt < olderThanUtc)
            .ToList();

        var expiredAt = DateTimeOffset.UtcNow;
        foreach (var run in stale)
        {
            run.Expire(expiredAt);
        }

        return Task.FromResult(stale.Count);
    }

    public Task<WorkflowRun?> FindValidatedAsync(
        Guid workflowDefinitionId,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var match = string.IsNullOrWhiteSpace(correlationId)
            ? null
            : _runs.Values
                .Where(run => run.WorkflowDefinitionId == workflowDefinitionId
                    && run.CorrelationId == correlationId
                    && run.Status == WorkflowRunStatus.Validated)
                .OrderByDescending(run => run.StartedAt)
                .FirstOrDefault();

        return Task.FromResult(match);
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
