using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Abstractions.Persistence;

public interface IConfiguredPipelineRunRepository
{
    Task AddAsync(
        ConfiguredPipelineRunDto pipelineRun,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns the most recent runs, newest first. Backs the process-wide observability dashboard
    /// so it reflects runs executed in any host (API or Worker), not just the current process's in-memory metrics.
    /// </summary>
    Task<IReadOnlyList<ConfiguredPipelineRunDto>> GetRecentAsync(
        int count,
        CancellationToken cancellationToken);

    Task SetEnabledAsync(
        Guid pipelineRunId,
        bool isEnabled,
        CancellationToken cancellationToken);

    /// <summary>Backs Correlation Search — the one run (if any) that produced a given CorrelationId.</summary>
    Task<ConfiguredPipelineRunDto?> GetByCorrelationIdAsync(
        string correlationId,
        CancellationToken cancellationToken);
}
