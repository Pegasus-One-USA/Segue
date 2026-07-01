using FHIRBridge.Application.Security;
using Microsoft.AspNetCore.Authorization;

namespace FHIRBridge.Api.Security;

public sealed class UnifiedAdminRequirement : IAuthorizationRequirement
{
    public static readonly IReadOnlySet<string> AcceptedRoleNames = new HashSet<string>(
        [
            UnifiedRoles.GlobalAdmin,
            UnifiedRoles.TenantAdmin,
            "FHIRBridge.GlobalAdmin",
            "FHIRBridge.TenantAdmin",
            // Legacy role-claim names retained so tokens issued before the five-role migration still authorize.
            "SuperAdmin",
            "Admin",
            "FHIRBridge.SuperAdmin",
            "FHIRBridge.Admin"
        ],
        StringComparer.OrdinalIgnoreCase);
}
