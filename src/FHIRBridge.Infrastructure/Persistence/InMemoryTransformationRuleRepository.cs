using System.Collections.Concurrent;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Infrastructure.Persistence;

/// <summary>
/// Transformation rules for the no-database host profile (integration tests, local runs without a database). Every
/// query is <see cref="EfTransformationRuleRepository"/>'s predicate, unchanged, over an in-memory set — keep the two
/// in step; the EF class's comments explain each tier.
/// </summary>
public sealed class InMemoryTransformationRuleRepository : ITransformationRuleRepository
{
    private readonly ConcurrentDictionary<Guid, TransformationRule> _rules = new();

    private IEnumerable<TransformationRule> Rules => _rules.Values;

    public Task<IReadOnlyList<TransformationRule>> GetWorkflowScopedAsync(
        Guid resourcePipelineRouteId, Guid? destinationConfigurationId, string resourceType,
        string destinationField, string? sourceSystem, string? sourceField,
        CancellationToken cancellationToken) =>
        List(Rules.Where(x =>
            x.ExecutionPhase == TransformExecutionPhase.PostMapping &&
            x.Scope == TransformScope.Workflow &&
            x.ResourcePipelineRouteId == resourcePipelineRouteId &&
            (destinationConfigurationId == null || x.DestinationConfigurationId == null
                || x.DestinationConfigurationId == destinationConfigurationId) &&
            x.ResourceType == resourceType &&
            x.DestinationField == destinationField &&
            (x.SourceSystem == null || x.SourceSystem == sourceSystem) &&
            (x.SourceField == null || x.SourceField == sourceField)));

    public Task<IReadOnlyList<TransformationRule>> GetFieldScopedAsync(
        string resourceType, string destinationField, string? sourceSystem, string? sourceField,
        CancellationToken cancellationToken) =>
        List(Rules.Where(x =>
            x.ExecutionPhase == TransformExecutionPhase.PostMapping &&
            x.Scope == TransformScope.Field &&
            (x.ResourceType == null || x.ResourceType == resourceType) &&
            (x.DestinationField == null || x.DestinationField == destinationField) &&
            (x.SourceSystem == null || x.SourceSystem == sourceSystem) &&
            (x.SourceField == null || x.SourceField == sourceField)));

    public Task<IReadOnlyList<TransformationRule>> GetResourceTypeScopedAsync(
        string resourceType, string? destinationField, CancellationToken cancellationToken) =>
        List(Rules.Where(x =>
            x.ExecutionPhase == TransformExecutionPhase.PostMapping &&
            x.Scope == TransformScope.ResourceType &&
            x.ResourceType == resourceType &&
            (x.DestinationField == null || x.DestinationField == destinationField)));

    public Task<IReadOnlyList<TransformationRule>> GetDestinationTypeScopedAsync(
        DestinationType destinationType, string? destinationField, CancellationToken cancellationToken) =>
        List(Rules.Where(x =>
            x.ExecutionPhase == TransformExecutionPhase.PostMapping &&
            x.Scope == TransformScope.DestinationType &&
            x.DestinationType == destinationType &&
            (x.DestinationField == null || x.DestinationField == destinationField)));

    public Task<IReadOnlyList<TransformationRule>> GetGlobalScopedAsync(
        string? destinationField, CancellationToken cancellationToken) =>
        List(Rules.Where(x =>
            x.ExecutionPhase == TransformExecutionPhase.PostMapping &&
            x.Scope == TransformScope.Global &&
            (x.DestinationField == null || x.DestinationField == destinationField)));

    public Task<IReadOnlyList<TransformationRule>> GetPreMappingRulesAsync(
        Guid deIdentificationProfileId, string resourceType, CancellationToken cancellationToken) =>
        List(Rules
            .Where(x =>
                x.ExecutionPhase == TransformExecutionPhase.PreMapping &&
                x.DeIdentificationProfileId == deIdentificationProfileId &&
                (x.Scope == TransformScope.Global ||
                 (x.Scope == TransformScope.ResourceType && x.ResourceType == resourceType)))
            .OrderBy(x => x.Order));

    public Task<IReadOnlyList<TransformationRule>> GetFhirResourceRulesAsync(
        TransformScope scope, string resourceType, string? sourceField, DestinationType? destinationType,
        Guid? resourcePipelineRouteId, string? sourceSystem, CancellationToken cancellationToken) =>
        List(Rules
            .Where(x =>
                x.ExecutionPhase == TransformExecutionPhase.FhirResource &&
                x.Scope == scope &&
                (scope == TransformScope.Global || scope == TransformScope.DestinationType ||
                 x.ResourceType == resourceType) &&
                (destinationType == null || x.DestinationType == null || x.DestinationType == destinationType) &&
                (resourcePipelineRouteId == null || x.ResourcePipelineRouteId == resourcePipelineRouteId) &&
                (sourceSystem == null || x.SourceSystem == null || x.SourceSystem == sourceSystem) &&
                (sourceField == null || x.SourceField == sourceField))
            .OrderBy(x => x.Order));

    public Task<IReadOnlyList<TransformationRule>> ListAsync(
        TransformScope? scope, DestinationType? destinationType, string? resourceType, string? destinationField,
        Guid? resourcePipelineRouteId, string? sourceSystem, string? sourceField,
        TransformExecutionPhase? executionPhase, CancellationToken cancellationToken) =>
        List(Rules
            .Where(x => executionPhase == null
                ? x.ExecutionPhase != TransformExecutionPhase.FhirResource
                : x.ExecutionPhase == executionPhase)
            .Where(x => scope == null || x.Scope == scope)
            .Where(x => destinationType == null || x.DestinationType == destinationType)
            .Where(x => resourceType == null || x.ResourceType == resourceType)
            .Where(x => destinationField == null || x.DestinationField == destinationField)
            .Where(x => resourcePipelineRouteId == null || x.ResourcePipelineRouteId == resourcePipelineRouteId)
            .Where(x => sourceSystem == null || x.SourceSystem == sourceSystem)
            .Where(x => sourceField == null || x.SourceField == sourceField)
            .OrderBy(x => x.Scope).ThenBy(x => x.Order));

    public Task<IReadOnlyList<TransformationRule>> GetPendingWorkflowRulesAsync(
        IReadOnlyCollection<DestinationType> destinationTypes, string? owner, CancellationToken cancellationToken) =>
        List(PendingWorkflowRules(destinationTypes, owner));

    public Task<int> DeletePendingWorkflowRulesAsync(
        IReadOnlyCollection<DestinationType> destinationTypes, string? owner, CancellationToken cancellationToken) =>
        Task.FromResult(Remove(PendingWorkflowRules(destinationTypes, owner)));

    public Task<IReadOnlyList<TransformationRule>> GetPendingWorkflowScopedAsync(
        DestinationType destinationType, string resourceType, string destinationField, string? sourceSystem,
        string? sourceField, string? owner, CancellationToken cancellationToken) =>
        List(Rules.Where(x =>
            x.ExecutionPhase == TransformExecutionPhase.PostMapping &&
            x.Scope == TransformScope.Workflow &&
            x.ResourcePipelineRouteId == null &&
            (owner == null || x.CreatedBy == owner) &&
            x.DestinationType == destinationType &&
            x.ResourceType == resourceType &&
            x.DestinationField == destinationField &&
            (x.SourceSystem == null || x.SourceSystem == sourceSystem) &&
            (x.SourceField == null || x.SourceField == sourceField)));

    public Task<int> DeleteWorkflowRulesForDestinationAsync(
        Guid resourcePipelineRouteId, Guid destinationConfigurationId, DestinationType destinationType,
        bool includeUnattributed, CancellationToken cancellationToken) =>
        Task.FromResult(Remove(Rules.Where(x =>
            x.Scope == TransformScope.Workflow &&
            x.ResourcePipelineRouteId == resourcePipelineRouteId &&
            (x.DestinationConfigurationId == destinationConfigurationId
                || (includeUnattributed
                    && x.DestinationConfigurationId == null
                    && x.DestinationType == destinationType)))));

    public Task<TransformationRule?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(_rules.GetValueOrDefault(id));

    public Task AddAsync(TransformationRule rule, CancellationToken cancellationToken)
    {
        _rules[rule.Id] = rule;
        return Task.CompletedTask;
    }

    /// <summary>Rows are held by reference, so the caller's changes are already visible.</summary>
    public Task UpdateAsync(TransformationRule rule, CancellationToken cancellationToken)
    {
        _rules[rule.Id] = rule;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(TransformationRule rule, CancellationToken cancellationToken)
    {
        _rules.TryRemove(rule.Id, out _);
        return Task.CompletedTask;
    }

    private IEnumerable<TransformationRule> PendingWorkflowRules(IReadOnlyCollection<DestinationType> destinationTypes, string? owner) =>
        Rules.Where(x =>
            x.Scope == TransformScope.Workflow &&
            x.ResourcePipelineRouteId == null &&
            // EF translates DestinationType!.Value on a null column to "no match"; LINQ to Objects would throw.
            (destinationTypes.Count == 0 || (x.DestinationType != null && destinationTypes.Contains(x.DestinationType.Value))) &&
            (owner == null || x.CreatedBy == owner));

    private int Remove(IEnumerable<TransformationRule> doomed)
    {
        var removed = 0;
        foreach (var rule in doomed.ToList())
        {
            if (_rules.TryRemove(rule.Id, out _))
            {
                removed++;
            }
        }

        return removed;
    }

    private static Task<IReadOnlyList<TransformationRule>> List(IEnumerable<TransformationRule> rows) =>
        Task.FromResult<IReadOnlyList<TransformationRule>>(rows.ToList());
}
