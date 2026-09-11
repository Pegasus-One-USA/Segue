using FHIRBridge.Application.Services.Transforms;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;

namespace FHIRBridge.UnitTests.Validation;

/// <summary>
/// Test double for <see cref="IEffectiveRuleResolver"/> that always reports no applicable rules, so
/// <c>CreateMappingProfileRequestValidator</c>'s rule-vs-column-type cross-check no-ops. Used by tests that
/// construct <c>ConfigurationService</c> directly and only care about the other mapping-profile rules.
/// </summary>
public sealed class NoOpEffectiveRuleResolver : IEffectiveRuleResolver
{
    public Task<IReadOnlyList<TransformationRule>> ResolveAsync(
        DestinationType destinationType,
        string resourceType,
        string destinationField,
        Guid? resourcePipelineRouteId,
        string? sourceSystem,
        string? sourceField,
        CancellationToken cancellationToken,
        bool workflowScopedOnly = false,
        bool includePendingWorkflowRules = false) =>
        Task.FromResult<IReadOnlyList<TransformationRule>>([]);
}
