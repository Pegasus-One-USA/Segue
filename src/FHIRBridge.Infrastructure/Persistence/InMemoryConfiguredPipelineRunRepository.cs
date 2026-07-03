using System.Collections.Concurrent;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Infrastructure.Persistence;

public sealed class InMemoryConfiguredPipelineRunRepository : IConfiguredPipelineRunRepository
{
    private readonly List<ConfiguredPipelineRunDto> _runs = [];
    private readonly object _gate = new();

    public Task AddAsync(
        ConfiguredPipelineRunDto pipelineRun,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _runs.Add(pipelineRun);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ConfiguredPipelineRunDto>> GetRecentAsync(
        int count,
        CancellationToken cancellationToken)
    {
        var take = Math.Clamp(count, 1, 500);

        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<ConfiguredPipelineRunDto>>(
                _runs
                    .Where(x => x.IsEnabled)
                    .OrderByDescending(x => x.StartedOnUtc)
                    .Take(take)
                    .ToList());
        }
    }

    public Task SetEnabledAsync(
        Guid pipelineRunId,
        bool isEnabled,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var index = _runs.FindIndex(x => x.Id == pipelineRunId);
            if (index >= 0)
            {
                _runs[index] = _runs[index] with { IsEnabled = isEnabled };
            }
        }

        return Task.CompletedTask;
    }
}
