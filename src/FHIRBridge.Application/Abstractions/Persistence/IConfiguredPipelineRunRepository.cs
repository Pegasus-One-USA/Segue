using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Abstractions.Persistence;

public interface IConfiguredPipelineRunRepository
{
    Task AddAsync(
        ConfiguredPipelineRunDto pipelineRun,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ConfiguredPipelineRunDto>> GetRecentAsync(
        Guid tenantId,
        int count,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns the most recent runs across all tenants, newest first. Backs the process-wide observability dashboard
    /// so it reflects runs executed in any host (API or Worker), not just the current process's in-memory metrics.
    /// </summary>
    Task<IReadOnlyList<ConfiguredPipelineRunDto>> GetRecentAcrossTenantsAsync(
        int count,
        CancellationToken cancellationToken);

    Task SetEnabledAsync(
        Guid tenantId,
        Guid pipelineRunId,
        bool isEnabled,
        CancellationToken cancellationToken);
}
