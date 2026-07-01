using System.Collections.Concurrent;
using FHIRBridge.Runtime.Application.Abstractions.Persistence;
using FHIRBridge.Runtime.Domain.Entities;

namespace FHIRBridge.Runtime.Infrastructure.Persistence;

public sealed class InMemoryPipelineRunStore : IPipelineRunStore
{
    private readonly ConcurrentDictionary<Guid, PipelineRun> _pipelineRuns = new();
    private readonly ConcurrentDictionary<Guid, List<PipelineRunEvent>> _events = new();

    public Task AddAsync(PipelineRun pipelineRun, CancellationToken cancellationToken)
    {
        _pipelineRuns[pipelineRun.Id] = pipelineRun;
        _events.TryAdd(pipelineRun.Id, []);

        return Task.CompletedTask;
    }

    public Task UpdateAsync(PipelineRun pipelineRun, CancellationToken cancellationToken)
    {
        _pipelineRuns[pipelineRun.Id] = pipelineRun;

        return Task.CompletedTask;
    }

    public Task<PipelineRun?> GetAsync(Guid pipelineRunId, CancellationToken cancellationToken)
    {
        _pipelineRuns.TryGetValue(pipelineRunId, out var pipelineRun);

        return Task.FromResult(pipelineRun);
    }

    public Task<IReadOnlyList<PipelineRun>> GetRecentAsync(int count, CancellationToken cancellationToken)
    {
        var pipelineRuns = _pipelineRuns.Values
            .OrderByDescending(x => x.StartedOnUtc)
            .Take(count)
            .ToList();

        return Task.FromResult<IReadOnlyList<PipelineRun>>(pipelineRuns);
    }

    public Task AddEventAsync(PipelineRunEvent pipelineRunEvent, CancellationToken cancellationToken)
    {
        var events = _events.GetOrAdd(pipelineRunEvent.PipelineRunId, _ => []);

        lock (events)
        {
            events.Add(pipelineRunEvent);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PipelineRunEvent>> GetEventsAsync(Guid pipelineRunId, CancellationToken cancellationToken)
    {
        if (!_events.TryGetValue(pipelineRunId, out var events))
        {
            return Task.FromResult<IReadOnlyList<PipelineRunEvent>>([]);
        }

        lock (events)
        {
            return Task.FromResult<IReadOnlyList<PipelineRunEvent>>(events.OrderBy(x => x.OccurredOnUtc).ToList());
        }
    }
}
