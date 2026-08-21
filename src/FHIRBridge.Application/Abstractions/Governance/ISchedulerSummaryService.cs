using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Abstractions.Governance;

/// <summary>Real per-route Next Run/Last Run summary — backs the Scheduler screen (previously a stub).</summary>
public interface ISchedulerSummaryService
{
    Task<IReadOnlyList<SchedulerSummaryDto>> GetSummaryAsync(CancellationToken cancellationToken);
}
