using FHIRBridge.Application.Security;
using Microsoft.AspNetCore.Authorization;

namespace FHIRBridge.Api.Security;

public sealed class UnifiedAdminRequirement : IAuthorizationRequirement
{
    public static readonly IReadOnlySet<string> AcceptedRoleNames = new HashSet<string>(
        [
            UnifiedRoles.SuperAdmin,
            UnifiedRoles.Admin,
            "FHIRBridge.SuperAdmin",
            "FHIRBridge.Admin",
            // Legacy role-claim names retained so tokens issued before the SuperAdmin/Admin
            // rename (GlobalAdmin/TenantAdmin) — and the five-role migration before that —
            // still authorize.
            "GlobalAdmin",
            "TenantAdmin",
            "FHIRBridge.GlobalAdmin",
            "FHIRBridge.TenantAdmin"
        ],
        StringComparer.OrdinalIgnoreCase);
}
