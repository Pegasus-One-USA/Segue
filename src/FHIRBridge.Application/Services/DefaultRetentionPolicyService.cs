using FHIRBridge.Application.Abstractions.Governance;

namespace FHIRBridge.Application.Services;

public sealed class DefaultRetentionPolicyService : IRetentionPolicyService
{
    public RetentionPolicy GetPolicy(string resourceType)
    {
        return new RetentionPolicy(
            RetentionYears: 7,
            IsImmutableAuditRequired: true,
            RequiresPhiFreeAudit: true);
    }
}
