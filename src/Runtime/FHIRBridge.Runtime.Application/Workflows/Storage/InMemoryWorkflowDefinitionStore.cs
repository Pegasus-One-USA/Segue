using FHIRBridge.Runtime.Application.Abstractions.Pipeline;
using FHIRBridge.Runtime.Domain.Workflows;
using Microsoft.Extensions.DependencyInjection;

namespace FHIRBridge.Runtime.Application.Workflows.Storage;

public sealed class InMemoryWorkflowDefinitionStore : IWorkflowDefinitionStore
{
    private readonly Dictionary<Guid, WorkflowDefinition> _workflows = [];
    private readonly IServiceScopeFactory _scopeFactory;

    public InMemoryWorkflowDefinitionStore(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<WorkflowDefinition> SaveAsync(WorkflowDefinition workflowDefinition, CancellationToken cancellationToken)
    {
        // No ICurrentUserService available here (this store's project doesn't reference FHIRBridge.Application —
        // it's the non-relational dev/test fallback, not the production path), so CreatedBy/UpdatedBy stay null;
        // only the timestamps are preserved/stamped, mirroring SqlWorkflowDefinitionStore's logic.
        var existing = _workflows.GetValueOrDefault(workflowDefinition.Id);

        // Unlike SqlWorkflowDefinitionStore's delete-then-re-add, this in-memory store's edit path is a plain
        // upsert — `existing is null` unambiguously means a genuine new workflow, so the license workflow-quota
        // check is gated on exactly that, matching the real store's semantics without needing an equivalent
        // "genuine create" marker. This store is registered as a singleton and the real license guard is
        // scoped, so it's resolved through a fresh scope rather than taken as a direct constructor dependency
        // (the same captive-dependency-avoidance pattern this codebase already uses for other singletons that
        // need a scoped collaborator, e.g. ISystemSettingsCache/ICurrentTenantResolver).
        if (existing is null)
        {
            using var scope = _scopeFactory.CreateScope();
            var licenseGuard = scope.ServiceProvider.GetRequiredService<IPipelineRunLicenseGuard>();
            await licenseGuard.EnsureWorkflowQuotaAvailableAsync(cancellationToken);
        }

        var utcNow = DateTime.UtcNow;
        workflowDefinition.StampAudit(
            createdOnUtc: existing?.CreatedOnUtc ?? utcNow,
            createdBy: existing?.CreatedBy,
            updatedOnUtc: existing is not null ? utcNow : null,
            updatedBy: null);

        _workflows[workflowDefinition.Id] = workflowDefinition;
        return workflowDefinition;
    }

    public Task<IReadOnlyCollection<WorkflowDefinition>> ListAsync(CancellationToken cancellationToken)
    {
        IReadOnlyCollection<WorkflowDefinition> workflows = _workflows.Values
            .OrderBy(workflow => workflow.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Task.FromResult(workflows);
    }

    public Task<WorkflowDefinition?> GetAsync(Guid workflowId, CancellationToken cancellationToken)
    {
        _workflows.TryGetValue(workflowId, out var workflowDefinition);
        return Task.FromResult(workflowDefinition);
    }

    public Task DeleteAsync(Guid workflowId, CancellationToken cancellationToken)
    {
        _workflows.Remove(workflowId);
        return Task.CompletedTask;
    }
}
