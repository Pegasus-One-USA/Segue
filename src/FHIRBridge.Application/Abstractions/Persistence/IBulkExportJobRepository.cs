using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Application.Abstractions.Persistence;

/// <summary>Durable store for in-flight <see cref="BulkExportJob"/> checkpoints polled by <c>BulkExportPollWorker</c>.</summary>
public interface IBulkExportJobRepository
{
    Task AddAsync(BulkExportJob job, CancellationToken cancellationToken);

    Task<BulkExportJob?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Jobs due for another poll: <c>Status == Polling</c> and either never polled yet or
    /// <c>NextPollNotBeforeUtc &lt;= utcNow</c>. Rows are returned tracked (not <c>AsNoTracking</c>), unlike this
    /// repository's other reads — the Worker mutates and saves them back in the same scope.</summary>
    Task<IReadOnlyList<BulkExportJob>> GetPollableAsync(DateTime utcNow, int maxBatchSize, CancellationToken cancellationToken);

    /// <summary>Non-terminal (<c>Pending</c>/<c>Polling</c>) sibling jobs sharing the same <c>WorkflowRunId</c>,
    /// excluding <paramref name="excludingJobId"/> — used to tell whether a just-completed <c>WorkflowNode</c> job
    /// is the last one its run is waiting on.</summary>
    Task<IReadOnlyList<BulkExportJob>> GetPendingByWorkflowRunAsync(Guid workflowRunId, Guid excludingJobId, CancellationToken cancellationToken);

    Task UpdateAsync(BulkExportJob job, CancellationToken cancellationToken);

    /// <summary>Count of non-terminal (<c>Pending</c>/<c>Polling</c>) jobs against this SourceConnection, across every
    /// <see cref="BulkExportJob.SourcePath"/> — used as a client-side concurrency guard before kicking off a new
    /// export, so a caller finds out it's at capacity via a cheap local check instead of only via a 429 from the
    /// source server (see the FHIR Bulk Data server's own per-connection concurrency limits, e.g. athenahealth's
    /// 2-per-practice Preview / 5-per-practice Production caps).</summary>
    Task<int> CountActiveBySourceConnectionAsync(Guid sourceConnectionId, CancellationToken cancellationToken);
}
