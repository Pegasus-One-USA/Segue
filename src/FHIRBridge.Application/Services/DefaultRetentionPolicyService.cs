using FHIRBridge.Application.Abstractions.Governance;

namespace FHIRBridge.Application.Services;

public sealed class DefaultRetentionPolicyService : IRetentionPolicyService
{
    public RetentionPolicy GetPolicy(Guid tenantId, string resourceType)
    {
        return new RetentionPolicy(
            RetentionYears: 7,
            IsImmutableAuditRequired: true,
            RequiresPhiFreeAudit: true);
    }
}
