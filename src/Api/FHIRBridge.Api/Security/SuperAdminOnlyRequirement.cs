using FHIRBridge.Application.Security;
using Microsoft.AspNetCore.Authorization;

namespace FHIRBridge.Api.Security;

public sealed class SuperAdminOnlyRequirement : IAuthorizationRequirement
{
    public static readonly IReadOnlySet<string> AcceptedRoleNames = new HashSet<string>(
        [
            UnifiedRoles.SuperAdmin,
            "FHIRBridge.SuperAdmin",
            // Legacy role-claim name retained so tokens issued before the SuperAdmin rename
            // (GlobalAdmin) still authorize.
            "GlobalAdmin",
            "FHIRBridge.GlobalAdmin"
        ],
        StringComparer.OrdinalIgnoreCase);
}
