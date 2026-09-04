using System.Collections.Concurrent;
using FHIRBridge.Application.Abstractions.Pipeline;

namespace FHIRBridge.Application.Services;

public sealed class InMemoryPipelineRunTracker : IPipelineRunTracker
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();

    public void MarkRunning(Guid pipelineRunId, CancellationTokenSource cancellationSource) =>
        _running.TryAdd(pipelineRunId, cancellationSource);

    public void MarkComplete(Guid pipelineRunId)
    {
        if (_running.TryRemove(pipelineRunId, out var cancellationSource))
        {
            cancellationSource.Dispose();
        }
    }

    public bool IsRunning(Guid pipelineRunId) => _running.ContainsKey(pipelineRunId);

    public bool RequestCancellation(Guid pipelineRunId)
    {
        if (!_running.TryGetValue(pipelineRunId, out var cancellationSource))
        {
            return false;
        }

        cancellationSource.Cancel();
        return true;
    }
}
