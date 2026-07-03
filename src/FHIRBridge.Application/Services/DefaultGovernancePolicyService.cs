using FHIRBridge.Application.Abstractions.Governance;

namespace FHIRBridge.Application.Services;

public sealed class DefaultGovernancePolicyService : IGovernancePolicyService
{
    public Task<ResourceGovernanceDecision> EvaluateAsync(
        ResourceGovernanceContext context,
        CancellationToken cancellationToken)
    {
        IReadOnlyCollection<string> policies =
        [
            "PhiFreeAudit",
            "ResourceLevelAccessAudit"
        ];

        return Task.FromResult(new ResourceGovernanceDecision(
            true,
            false,
            null,
            policies));
    }
}
