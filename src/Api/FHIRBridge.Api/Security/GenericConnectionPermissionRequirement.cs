using FHIRBridge.Application.Security;
using Microsoft.AspNetCore.Authorization;

namespace FHIRBridge.Api.Security;

/// <summary>
/// Backs every auto-registered <c>HasPermission:sourceconnections.*</c> / <c>destinationconnections.*</c>
/// policy with an OR against the corresponding vendor-specific permission for the same action, instead of a
/// plain single-code membership check (<see cref="PermissionRequirement"/>). Before this, the generic
/// Settings-page permission was the ONLY way into the source/destination connection list and CRUD endpoints
/// — a role scoped to just <c>epic.create</c> had no path to them at all, even though it can already fully
/// manage Epic nodes inside a workflow. See <see cref="GenericConnectionPermissionAuthorizationHandler"/> for
/// the actual check. Registered only for codes in the <see cref="PermissionGroupCode.SourceConnections"/> /
/// <see cref="PermissionGroupCode.DestinationConnections"/> groups (see <see cref="TryParse"/> and its one
/// call site, Program.cs's policy-registration loop) — every other permission code keeps using
/// <see cref="PermissionRequirement"/> unchanged.
/// </summary>
public sealed class GenericConnectionPermissionRequirement : IAuthorizationRequirement
{
    public GenericConnectionPermissionRequirement(PermissionGroupCode genericGroup, PermissionActionCode action)
    {
        GenericGroup = genericGroup;
        Action = action;
    }

    /// <summary>Always <see cref="PermissionGroupCode.SourceConnections"/> or
    /// <see cref="PermissionGroupCode.DestinationConnections"/> — enforced by <see cref="TryParse"/>, the
    /// only place this requirement is constructed.</summary>
    public PermissionGroupCode GenericGroup { get; }

    public PermissionActionCode Action { get; }

    public string GenericCode => PermissionTaxonomy.BuildPermissionCode(GenericGroup, Action);

    /// <summary>
    /// True, with <paramref name="group"/>/<paramref name="action"/> populated, when <paramref name="code"/>
    /// is a SourceConnections/DestinationConnections code (e.g. "sourceconnections.view") — the wire format
    /// is always "{group}.{action}" (see <see cref="PermissionTaxonomy.BuildPermissionCode"/>), so this just
    /// matches the known generic-group prefixes and parses the remainder back into a
    /// <see cref="PermissionActionCode"/>.
    /// </summary>
    public static bool TryParse(string code, out PermissionGroupCode group, out PermissionActionCode action)
    {
        group = default;
        action = default;

        string prefix;
        if (code.StartsWith("sourceconnections.", StringComparison.OrdinalIgnoreCase))
        {
            prefix = "sourceconnections.";
            group = PermissionGroupCode.SourceConnections;
        }
        else if (code.StartsWith("destinationconnections.", StringComparison.OrdinalIgnoreCase))
        {
            prefix = "destinationconnections.";
            group = PermissionGroupCode.DestinationConnections;
        }
        else
        {
            return false;
        }

        return Enum.TryParse(code[prefix.Length..], ignoreCase: true, out action);
    }
}
