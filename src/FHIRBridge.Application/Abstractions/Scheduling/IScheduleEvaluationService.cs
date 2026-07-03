using FHIRBridge.Application.Scheduling;

namespace FHIRBridge.Application.Abstractions.Scheduling;

/// <summary>
/// Evaluates scheduled-pull routes and claims the ones that are due.
/// </summary>
public interface IScheduleEvaluationService
{
    /// <summary>
    /// Finds every route whose schedule is due as of <paramref name="utcNow"/> (catch-up aware), stamps each
    /// claimed route's <c>LastTriggeredOnUtc</c> so it is not claimed again, persists the change, and returns the
    /// claimed due runs. This mutates state — it claims work, it does not merely query it.
    /// </summary>
    Task<IReadOnlyList<DueScheduledRun>> ClaimDueRunsAsync(DateTime utcNow, CancellationToken cancellationToken);
}
