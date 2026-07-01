using FHIRBridge.Application.Abstractions.Governance;

namespace FHIRBridge.Infrastructure.Governance;

/// <summary>Phase G5 — denies delivery when patient/tenant consent does not permit the resource type.</summary>
public sealed class ConsentGovernanceRule : IGovernanceRule
{
    public const string PolicyName = "ConsentEnforcement";

    private readonly IConsentService _consentService;

    public ConsentGovernanceRule(IConsentService consentService)
    {
        _consentService = consentService;
    }

    public Task<GovernanceRuleResult> EvaluateAsync(
        ResourceGovernanceContext context,
        CancellationToken cancellationToken)
    {
        var decision = _consentService.Evaluate(context.TenantId, context.ResourceType, context.ResourceId);

        return Task.FromResult(decision.IsPermitted
            ? GovernanceRuleResult.Allow(PolicyName)
            : GovernanceRuleResult.Deny(PolicyName, decision.Reason ?? "Consent denied."));
    }
}
