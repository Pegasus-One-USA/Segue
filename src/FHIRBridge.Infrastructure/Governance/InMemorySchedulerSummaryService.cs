using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Infrastructure.Governance;

/// <summary>No database configured — nothing to summarize.</summary>
public sealed class InMemorySchedulerSummaryService : ISchedulerSummaryService
{
    public Task<IReadOnlyList<SchedulerSummaryDto>> GetSummaryAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<SchedulerSummaryDto>>([]);
}
