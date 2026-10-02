using System.Collections.Concurrent;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Infrastructure.Persistence;

// In-memory twins of five small EF repositories, for the no-database host profile (integration tests, local runs
// without a database), which previously had no registration for them at all, so every screen that touched one
// failed. Each query mirrors its Ef* counterpart's predicate; keep them in step. Rows are held by reference, so a
// caller's changes to a returned entity are visible without a save, as in InMemoryEhrWriteLedgerRepository.

public sealed class InMemoryDeIdentificationProfileRepository : IDeIdentificationProfileRepository
{
    private readonly ConcurrentDictionary<Guid, DeIdentificationProfile> _profiles = new();

    public Task<IReadOnlyList<DeIdentificationProfile>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DeIdentificationProfile>>(_profiles.Values.OrderBy(x => x.Name).ToList());

    public Task<DeIdentificationProfile?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(_profiles.GetValueOrDefault(id));

    public Task AddAsync(DeIdentificationProfile profile, CancellationToken cancellationToken)
    {
        _profiles[profile.Id] = profile;
        return Task.CompletedTask;
    }
}

public sealed class InMemoryResourceTypeCriteriaRepository : IResourceTypeCriteriaRepository
{
    private readonly ConcurrentDictionary<Guid, ResourceTypeCriteria> _criteria = new();

    public Task<IReadOnlyList<ResourceTypeCriteria>> ListForWorkflowAsync(Guid workflowId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ResourceTypeCriteria>>(_criteria.Values
            .Where(x => x.WorkflowId == workflowId)
            .OrderBy(x => x.SourceNodeId, StringComparer.Ordinal)
            .ThenBy(x => x.ResourceType, StringComparer.Ordinal)
            .ToList());

    public Task<ResourceTypeCriteria?> GetAsync(
        Guid workflowId, string sourceNodeId, string resourceType, CancellationToken cancellationToken) =>
        Task.FromResult(_criteria.Values.FirstOrDefault(x =>
            x.WorkflowId == workflowId && x.SourceNodeId == sourceNodeId && x.ResourceType == resourceType));

    public Task AddAsync(ResourceTypeCriteria criteria, CancellationToken cancellationToken)
    {
        _criteria[criteria.Id] = criteria;
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>The EF store soft-deletes; here the row simply goes, which reads the same to every caller.</summary>
    public Task RemoveAsync(ResourceTypeCriteria criteria, CancellationToken cancellationToken)
    {
        _criteria.TryRemove(criteria.Id, out _);
        return Task.CompletedTask;
    }

    public Task RemoveForWorkflowAsync(Guid workflowId, CancellationToken cancellationToken)
    {
        foreach (var row in _criteria.Values.Where(x => x.WorkflowId == workflowId).ToList())
        {
            _criteria.TryRemove(row.Id, out _);
        }

        return Task.CompletedTask;
    }
}

public sealed class InMemorySchemaMappingRepository : ISchemaMappingRepository
{
    private readonly ConcurrentDictionary<Guid, SchemaMapping> _mappings = new();

    public Task<IReadOnlyList<SchemaMapping>> GetApprovedAsync(
        string sourceSystem, string resourceType, string destinationTable, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SchemaMapping>>(_mappings.Values
            .Where(x =>
                x.SourceSystem == sourceSystem &&
                x.ResourceType == resourceType &&
                x.DestinationTable == destinationTable &&
                x.Status == SchemaMappingStatus.Approved)
            .ToList());

    public Task<SchemaMapping?> FindAsync(
        string sourceSystem, string resourceType, string destinationTable, string destinationField,
        CancellationToken cancellationToken) =>
        Task.FromResult(_mappings.Values.FirstOrDefault(x =>
            x.SourceSystem == sourceSystem &&
            x.ResourceType == resourceType &&
            x.DestinationTable == destinationTable &&
            x.DestinationField == destinationField));

    public Task AddAsync(SchemaMapping mapping, CancellationToken cancellationToken)
    {
        _mappings[mapping.Id] = mapping;
        return Task.CompletedTask;
    }

    public Task UpdateAsync(SchemaMapping mapping, CancellationToken cancellationToken)
    {
        _mappings[mapping.Id] = mapping;
        return Task.CompletedTask;
    }
}

public sealed class InMemoryUserFhirContextBindingRepository : IUserFhirContextBindingRepository
{
    private readonly ConcurrentDictionary<Guid, UserFhirContextBinding> _bindings = new();

    public Task<UserFhirContextBinding?> GetAsync(Guid sourceConnectionId, string userIdentity, CancellationToken cancellationToken) =>
        Task.FromResult(_bindings.Values.FirstOrDefault(x =>
            x.SourceConnectionId == sourceConnectionId && x.UserIdentity == userIdentity));

    public Task<UserFhirContextBinding?> GetByResourceAsync(
        Guid sourceConnectionId, FhirContextResourceType resourceType, string resourceId, CancellationToken cancellationToken) =>
        Task.FromResult(_bindings.Values.FirstOrDefault(x =>
            x.SourceConnectionId == sourceConnectionId && x.ResourceType == resourceType && x.ResourceId == resourceId));

    public Task AddAsync(UserFhirContextBinding binding, CancellationToken cancellationToken)
    {
        _bindings[binding.Id] = binding;
        return Task.CompletedTask;
    }
}

public sealed class InMemoryBulkExportJobRepository : IBulkExportJobRepository
{
    private readonly ConcurrentDictionary<Guid, BulkExportJob> _jobs = new();

    public Task AddAsync(BulkExportJob job, CancellationToken cancellationToken)
    {
        _jobs[job.Id] = job;
        return Task.CompletedTask;
    }

    public Task<BulkExportJob?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(_jobs.GetValueOrDefault(id));

    public Task<IReadOnlyList<BulkExportJob>> GetPollableAsync(DateTime utcNow, int maxBatchSize, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<BulkExportJob>>(_jobs.Values
            .Where(x => x.Status == BulkExportJobStatus.Polling)
            .Where(x => x.NextPollNotBeforeUtc == null || x.NextPollNotBeforeUtc <= utcNow)
            .OrderBy(x => x.KickedOffOnUtc)
            .Take(Math.Clamp(maxBatchSize, 1, 500))
            .ToList());

    public Task<IReadOnlyList<BulkExportJob>> GetPendingByWorkflowRunAsync(
        Guid workflowRunId, Guid excludingJobId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<BulkExportJob>>(_jobs.Values
            .Where(x => x.WorkflowRunId == workflowRunId && x.Id != excludingJobId)
            .Where(x => x.Status == BulkExportJobStatus.Pending || x.Status == BulkExportJobStatus.Polling)
            .ToList());

    public Task<BulkExportJob?> GetLatestByWorkflowRunAsync(Guid workflowRunId, CancellationToken cancellationToken) =>
        Task.FromResult(_jobs.Values
            .Where(x => x.WorkflowRunId == workflowRunId)
            .OrderByDescending(x => x.KickedOffOnUtc)
            .FirstOrDefault());

    public Task<int> CountActiveBySourceConnectionAsync(Guid sourceConnectionId, CancellationToken cancellationToken) =>
        Task.FromResult(_jobs.Values.Count(x =>
            x.SourceConnectionId == sourceConnectionId &&
            (x.Status == BulkExportJobStatus.Pending || x.Status == BulkExportJobStatus.Polling)));

    public Task UpdateAsync(BulkExportJob job, CancellationToken cancellationToken)
    {
        _jobs[job.Id] = job;
        return Task.CompletedTask;
    }
}
