using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.Abstractions.Persistence;

/// <summary>Persistence for <see cref="TransformationRule"/>. One method per scope tier so
/// <c>EffectiveRuleResolver</c> can query exactly the tier it's currently checking, plus a general
/// <see cref="ListAsync"/> for the Rules modal's "everything configured here" view.</summary>
public interface ITransformationRuleRepository
{
    /// <summary><paramref name="sourceSystem"/> returns rows with no source restriction OR matching this
    /// source (resolver prefers the latter — same pattern as the ResourceType/DestinationType tiers'
    /// destinationField parameter).</summary>
    Task<IReadOnlyList<TransformationRule>> GetWorkflowScopedAsync(
        Guid resourcePipelineRouteId, string resourceType, string destinationField, string? sourceSystem,
        string? sourceField, CancellationToken cancellationToken);

    Task<IReadOnlyList<TransformationRule>> GetFieldScopedAsync(
        string resourceType, string destinationField, string? sourceSystem, string? sourceField,
        CancellationToken cancellationToken);

    /// <summary><paramref name="destinationField"/> null returns every ResourceType-scoped rule regardless of
    /// field; non-null returns rules with no field restriction OR matching this field (resolver prefers the latter).</summary>
    Task<IReadOnlyList<TransformationRule>> GetResourceTypeScopedAsync(
        string resourceType, string? destinationField, CancellationToken cancellationToken);

    Task<IReadOnlyList<TransformationRule>> GetDestinationTypeScopedAsync(
        DestinationType destinationType, string? destinationField, CancellationToken cancellationToken);

    Task<IReadOnlyList<TransformationRule>> GetGlobalScopedAsync(
        string? destinationField, CancellationToken cancellationToken);

    /// <summary>Pre-mapping (raw-JSON, by <c>SourceField</c> path) rules belonging to one de-identification
    /// profile — Global-scoped rows apply regardless of resource type, ResourceType-scoped rows apply only to
    /// the given type. Used by <c>SafeHarborDeIdentificationService</c>, not the post-mapping resolver.</summary>
    Task<IReadOnlyList<TransformationRule>> GetPreMappingRulesAsync(
        Guid deIdentificationProfileId, string resourceType, CancellationToken cancellationToken);

    Task<IReadOnlyList<TransformationRule>> ListAsync(
        TransformScope? scope, DestinationType? destinationType, string? resourceType, string? destinationField,
        Guid? resourcePipelineRouteId, string? sourceSystem, string? sourceField, CancellationToken cancellationToken);

    Task<TransformationRule?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task AddAsync(TransformationRule rule, CancellationToken cancellationToken);

    Task UpdateAsync(TransformationRule rule, CancellationToken cancellationToken);

    Task DeleteAsync(TransformationRule rule, CancellationToken cancellationToken);
}
