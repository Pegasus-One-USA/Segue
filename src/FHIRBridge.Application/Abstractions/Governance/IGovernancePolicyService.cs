namespace FHIRBridge.Application.Abstractions.Governance;

public interface IGovernancePolicyService
{
    Task<ResourceGovernanceDecision> EvaluateAsync(
        ResourceGovernanceContext context,
        CancellationToken cancellationToken);
}

public sealed record ResourceGovernanceContext(
    Guid PipelineRunId,
    Guid? RouteId,
    string ResourceType,
    string? ResourceId,
    string Action,
    string? TriggeredBy,
    string? CorrelationId,
    Guid? DestinationConfigurationId = null);

public sealed record ResourceGovernanceDecision(
    bool IsAllowed,
    Guid? DeIdentificationProfileId,
    string? DenialReason,
    IReadOnlyCollection<string> AppliedPolicies)
{
    public bool RequiresDeIdentification => DeIdentificationProfileId is not null;
}
