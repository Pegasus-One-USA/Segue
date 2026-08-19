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
    Guid? DeIdentificationProfileId,
    string? DenialReason,
    string PolicyName)
{
    public static GovernanceRuleResult Allow(string policyName, Guid? deIdentificationProfileId = null)
        => new(true, deIdentificationProfileId, null, policyName);

    public static GovernanceRuleResult Deny(string policyName, string reason)
        => new(false, null, reason, policyName);
}
