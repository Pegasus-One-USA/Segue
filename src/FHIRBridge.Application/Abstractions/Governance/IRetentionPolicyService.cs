namespace FHIRBridge.Application.Abstractions.Governance;

public interface IRetentionPolicyService
{
    RetentionPolicy GetPolicy(string resourceType);
}

public sealed record RetentionPolicy(
    int RetentionYears,
    bool IsImmutableAuditRequired,
    bool RequiresPhiFreeAudit);
