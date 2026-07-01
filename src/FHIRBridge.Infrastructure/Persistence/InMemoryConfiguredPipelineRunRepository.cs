using System.Collections.Concurrent;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Infrastructure.Persistence;

public sealed class InMemoryConfiguredPipelineRunRepository : IConfiguredPipelineRunRepository
{
    private readonly ConcurrentDictionary<Guid, List<ConfiguredPipelineRunDto>> _runs = new();

    public Task AddAsync(
        ConfiguredPipelineRunDto pipelineRun,
        CancellationToken cancellationToken)
    {
        var tenantRuns = _runs.GetOrAdd(pipelineRun.TenantId, _ => []);

        lock (tenantRuns)
        {
            tenantRuns.Add(pipelineRun);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ConfiguredPipelineRunDto>> GetRecentAsync(
        Guid tenantId,
        int count,
        CancellationToken cancellationToken)
    {
        if (!_runs.TryGetValue(tenantId, out var tenantRuns))
        {
            return Task.FromResult<IReadOnlyList<ConfiguredPipelineRunDto>>([]);
        }

        var take = Math.Clamp(count, 1, 500);

        lock (tenantRuns)
        {
            return Task.FromResult<IReadOnlyList<ConfiguredPipelineRunDto>>(
                tenantRuns
                    .Where(x => x.IsEnabled)
                    .OrderByDescending(x => x.StartedOnUtc)
                    .Take(take)
                    .ToList());
        }
    }

    public Task<IReadOnlyList<ConfiguredPipelineRunDto>> GetRecentAcrossTenantsAsync(
        int count,
        CancellationToken cancellationToken)
    {
        var take = Math.Clamp(count, 1, 1000);
        var all = _runs.Values.SelectMany(tenantRuns =>
        {
            lock (tenantRuns)
            {
                return tenantRuns.ToList();
            }
        });

        return Task.FromResult<IReadOnlyList<ConfiguredPipelineRunDto>>(
            all.Where(x => x.IsEnabled)
                .OrderByDescending(x => x.StartedOnUtc)
                .Take(take)
                .ToList());
    }

    public Task SetEnabledAsync(
        Guid tenantId,
        Guid pipelineRunId,
        bool isEnabled,
        CancellationToken cancellationToken)
    {
        if (!_runs.TryGetValue(tenantId, out var tenantRuns))
        {
            return Task.CompletedTask;
        }

        lock (tenantRuns)
        {
            var index = tenantRuns.FindIndex(x => x.Id == pipelineRunId);
            if (index >= 0)
            {
                tenantRuns[index] = tenantRuns[index] with { IsEnabled = isEnabled };
            }
        }

        return Task.CompletedTask;
    }
}
