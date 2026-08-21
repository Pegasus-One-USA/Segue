using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Persistence;

public sealed class EfBulkExportJobRepository : IBulkExportJobRepository
{
    private readonly FHIRBridgeDbContext _dbContext;

    public EfBulkExportJobRepository(FHIRBridgeDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task AddAsync(BulkExportJob job, CancellationToken cancellationToken)
    {
        _dbContext.BulkExportJobs.Add(job);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public Task<BulkExportJob?> GetAsync(Guid id, CancellationToken cancellationToken)
        => _dbContext.BulkExportJobs.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public Task<IReadOnlyList<BulkExportJob>> GetPollableAsync(DateTime utcNow, int maxBatchSize, CancellationToken cancellationToken)
        => QueryPollableAsync(utcNow, maxBatchSize, cancellationToken);

    private async Task<IReadOnlyList<BulkExportJob>> QueryPollableAsync(DateTime utcNow, int maxBatchSize, CancellationToken cancellationToken)
    {
        // Tracked deliberately (no AsNoTracking) — the Worker mutates and saves these back in the same scope, and
        // relies on the RowVersion concurrency token to detect a losing race against another Worker instance.
        var take = Math.Clamp(maxBatchSize, 1, 500);
        return await _dbContext.BulkExportJobs
            .Where(x => x.Status == BulkExportJobStatus.Polling)
            .Where(x => x.NextPollNotBeforeUtc == null || x.NextPollNotBeforeUtc <= utcNow)
            .OrderBy(x => x.KickedOffOnUtc)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<BulkExportJob>> GetPendingByWorkflowRunAsync(
        Guid workflowRunId, Guid excludingJobId, CancellationToken cancellationToken)
    {
        return await _dbContext.BulkExportJobs
            .AsNoTracking()
            .Where(x => x.WorkflowRunId == workflowRunId && x.Id != excludingJobId)
            .Where(x => x.Status == BulkExportJobStatus.Pending || x.Status == BulkExportJobStatus.Polling)
            .ToListAsync(cancellationToken);
    }

    public async Task UpdateAsync(BulkExportJob job, CancellationToken cancellationToken)
    {
        if (_dbContext.Entry(job).State == EntityState.Detached)
        {
            _dbContext.BulkExportJobs.Update(job);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
