using FHIRBridge.Runtime.Domain.Entities;

namespace FHIRBridge.Runtime.Application.Abstractions.Persistence;

public interface IPipelineRunStore
{
    Task AddAsync(PipelineRun pipelineRun, CancellationToken cancellationToken);

    Task UpdateAsync(PipelineRun pipelineRun, CancellationToken cancellationToken);

    Task<PipelineRun?> GetAsync(Guid pipelineRunId, CancellationToken cancellationToken);

    Task<IReadOnlyList<PipelineRun>> GetRecentAsync(int count, CancellationToken cancellationToken);

    Task AddEventAsync(PipelineRunEvent pipelineRunEvent, CancellationToken cancellationToken);

    Task<IReadOnlyList<PipelineRunEvent>> GetEventsAsync(Guid pipelineRunId, CancellationToken cancellationToken);
}
