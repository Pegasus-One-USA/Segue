using FHIRBridge.Application.Abstractions.Governance;

namespace FHIRBridge.Infrastructure.Governance;

/// <summary>Phase G1 — enforces per-resource-type RBAC by denying access to resource types that are not permitted.</summary>
public sealed class ResourceTypeAccessGovernanceRule : IGovernanceRule
{
    public const string PolicyName = "ResourceTypeRbac";

    private readonly IResourceTypeAccessPolicy _accessPolicy;

    public ResourceTypeAccessGovernanceRule(IResourceTypeAccessPolicy accessPolicy)
    {
        _accessPolicy = accessPolicy;
    }

    public Task<GovernanceRuleResult> EvaluateAsync(
        ResourceGovernanceContext context,
        CancellationToken cancellationToken)
    {
        var allowed = _accessPolicy.IsResourceTypeAllowed(context.ResourceType);

        return Task.FromResult(allowed
            ? GovernanceRuleResult.Allow(PolicyName)
            : GovernanceRuleResult.Deny(
                PolicyName,
                $"Access to resource type '{context.ResourceType}' is not permitted."));
    }
}
