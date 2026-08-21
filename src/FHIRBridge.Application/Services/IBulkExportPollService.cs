namespace FHIRBridge.Application.Services;

/// <summary>Drives one poll tick over every due <c>BulkExportJob</c> — the testable logic behind
/// <c>BulkExportPollWorker</c> (a thin timer/scope shell, same split as <c>IScheduleDispatcher</c>/
/// <c>ScheduleDispatcherWorker</c>).</summary>
public interface IBulkExportPollService
{
    Task PollDueJobsAsync(int maxBatchSize, int defaultPollIntervalSeconds, int maxPollAttempts, CancellationToken cancellationToken);
}
