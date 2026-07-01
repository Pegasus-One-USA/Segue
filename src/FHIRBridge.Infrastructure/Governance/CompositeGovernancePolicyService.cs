using FHIRBridge.Application.Abstractions.Governance;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Governance;

/// <summary>
/// Evaluates all registered <see cref="IGovernanceRule"/>s for a resource access. Access is denied as soon as any rule
/// denies; de-identification is required if any rule requires it; the applied-policy names are aggregated. Always
/// includes the baseline platform policies (tenant isolation, PHI-free audit) so the audit trail is unchanged when no
/// custom rules deny.
/// </summary>
public sealed class CompositeGovernancePolicyService : IGovernancePolicyService
{
    private static readonly string[] BaselinePolicies =
    [
        "TenantIsolation",
        "PhiFreeAudit",
        "ResourceLevelAccessAudit"
    ];

    private readonly IReadOnlyList<IGovernanceRule> _rules;
    private readonly ILogger<CompositeGovernancePolicyService> _logger;

    public CompositeGovernancePolicyService(
        IEnumerable<IGovernanceRule> rules,
        ILogger<CompositeGovernancePolicyService> logger)
    {
        _rules = rules.ToList();
        _logger = logger;
    }

    public async Task<ResourceGovernanceDecision> EvaluateAsync(
        ResourceGovernanceContext context,
        CancellationToken cancellationToken)
    {
        var appliedPolicies = new List<string>(BaselinePolicies);
        var requiresDeIdentification = false;

        foreach (var rule in _rules)
        {
            var result = await rule.EvaluateAsync(context, cancellationToken);
            appliedPolicies.Add(result.PolicyName);
            requiresDeIdentification |= result.RequiresDeIdentification;

            if (!result.IsAllowed)
            {
                _logger.LogWarning(
                    "Governance denied {ResourceType}/{ResourceId} for tenant {TenantId} by policy {PolicyName}: {Reason}",
                    context.ResourceType, context.ResourceId, context.TenantId, result.PolicyName, result.DenialReason);

                return new ResourceGovernanceDecision(
                    IsAllowed: false,
                    RequiresDeIdentification: requiresDeIdentification,
                    DenialReason: result.DenialReason,
                    AppliedPolicies: appliedPolicies);
            }
        }

        return new ResourceGovernanceDecision(
            IsAllowed: true,
            RequiresDeIdentification: requiresDeIdentification,
            DenialReason: null,
            AppliedPolicies: appliedPolicies);
    }
}
