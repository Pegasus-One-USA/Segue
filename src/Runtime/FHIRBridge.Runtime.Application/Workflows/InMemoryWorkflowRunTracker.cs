using System.Collections.Concurrent;

namespace FHIRBridge.Runtime.Application.Workflows;

public sealed class InMemoryWorkflowRunTracker : IWorkflowRunTracker
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();

    public void MarkRunning(Guid workflowRunId, CancellationTokenSource cancellationSource) =>
        _running.TryAdd(workflowRunId, cancellationSource);

    public void MarkComplete(Guid workflowRunId)
    {
        if (_running.TryRemove(workflowRunId, out var cancellationSource))
        {
            cancellationSource.Dispose();
        }
    }

    public bool IsRunning(Guid workflowRunId) => _running.ContainsKey(workflowRunId);

    public bool RequestCancellation(Guid workflowRunId)
    {
        if (!_running.TryGetValue(workflowRunId, out var cancellationSource))
        {
            return false;
        }

        cancellationSource.Cancel();
        return true;
    }
}
