namespace FHIRBridge.Application.Abstractions.Governance;

/// <summary>
/// A single composable governance check (RBAC, consent, de-identification, …). The composite policy service runs all
/// registered rules: access is denied if any rule denies, de-identification is required if any rule requires it, and
/// the applied-policy names are aggregated for the audit trail.
/// </summary>
public interface IGovernanceRule
{
    Task<GovernanceRuleResult> EvaluateAsync(
        ResourceGovernanceContext context,
        CancellationToken cancellationToken);
}

public sealed record GovernanceRuleResult(
    bool IsAllowed,
    bool RequiresDeIdentification,
    string? DenialReason,
    string PolicyName)
{
    public static GovernanceRuleResult Allow(string policyName, bool requiresDeIdentification = false)
        => new(true, requiresDeIdentification, null, policyName);

    public static GovernanceRuleResult Deny(string policyName, string reason)
        => new(false, false, reason, policyName);
}
