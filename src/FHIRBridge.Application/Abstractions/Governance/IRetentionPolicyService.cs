namespace FHIRBridge.Application.Abstractions.Governance;

public interface IRetentionPolicyService
{
    RetentionPolicy GetPolicy(Guid tenantId, string resourceType);
}

public sealed record RetentionPolicy(
    int RetentionYears,
    bool IsImmutableAuditRequired,
    bool RequiresPhiFreeAudit);
