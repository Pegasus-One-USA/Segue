namespace FHIRBridge.Application.Abstractions.Scheduling;

/// <summary>
/// Claims due scheduled runs and publishes a pipeline-run command for each to the messaging transport.
/// Invoked on a timer by the dispatcher worker role (or on demand).
/// </summary>
public interface IScheduleDispatcher
{
    /// <summary>Claims due runs as of <paramref name="utcNow"/>, enqueues a command per run, and returns the count enqueued.</summary>
    Task<int> DispatchDueRunsAsync(DateTime utcNow, CancellationToken cancellationToken);
}
