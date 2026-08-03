using System.Collections.Concurrent;

namespace FHIRBridge.Runtime.Application.Workflows;

public sealed class InMemoryWorkflowRunTracker : IWorkflowRunTracker
{
    private readonly ConcurrentDictionary<Guid, byte> _running = new();

    public void MarkRunning(Guid workflowRunId) => _running.TryAdd(workflowRunId, 0);

    public void MarkComplete(Guid workflowRunId) => _running.TryRemove(workflowRunId, out _);

    public bool IsRunning(Guid workflowRunId) => _running.ContainsKey(workflowRunId);
}
