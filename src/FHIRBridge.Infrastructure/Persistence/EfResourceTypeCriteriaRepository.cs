using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Persistence;

public sealed class EfResourceTypeCriteriaRepository : IResourceTypeCriteriaRepository
{
    private readonly FHIRBridgeDbContext _db;

    public EfResourceTypeCriteriaRepository(FHIRBridgeDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<ResourceTypeCriteria>> ListForWorkflowAsync(
        Guid workflowId, CancellationToken cancellationToken) =>
        await _db.ResourceTypeCriteria
            .AsNoTracking()
            .Where(x => x.WorkflowId == workflowId)
            .OrderBy(x => x.SourceNodeId)
            .ThenBy(x => x.ResourceType)
            .ToListAsync(cancellationToken);

    public Task<ResourceTypeCriteria?> GetAsync(
        Guid workflowId, string sourceNodeId, string resourceType, CancellationToken cancellationToken) =>
        _db.ResourceTypeCriteria.FirstOrDefaultAsync(
            x => x.WorkflowId == workflowId
                && x.SourceNodeId == sourceNodeId
                && x.ResourceType == resourceType,
            cancellationToken);

    public async Task AddAsync(ResourceTypeCriteria criteria, CancellationToken cancellationToken)
    {
        await _db.ResourceTypeCriteria.AddAsync(criteria, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        _db.SaveChangesAsync(cancellationToken);

    public async Task RemoveAsync(ResourceTypeCriteria criteria, CancellationToken cancellationToken)
    {
        // Soft-deleted by AuditingSaveChangesInterceptor (ISoftDeletable), so the row stays for audit and the
        // filtered unique index lets the same resource type get criteria again later.
        _db.ResourceTypeCriteria.Remove(criteria);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveForWorkflowAsync(Guid workflowId, CancellationToken cancellationToken)
    {
        // Tracked (not ExecuteDelete) so the auditing interceptor converts these into soft deletes with
        // provenance, exactly as a single-row delete gets.
        var rows = await _db.ResourceTypeCriteria
            .Where(x => x.WorkflowId == workflowId)
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            return;
        }

        _db.ResourceTypeCriteria.RemoveRange(rows);
        await _db.SaveChangesAsync(cancellationToken);
    }
}
