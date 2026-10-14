namespace FHIRBridge.Application.Abstractions.Security;

/// <summary>
/// Outcome of asking an external IdP which FHIRBridge roles a signed-in identity should hold.
/// </summary>
/// <param name="Succeeded">False when the IdP could not be queried (outage, misconfiguration).</param>
/// <param name="RoleNames">FHIRBridge role names granted by the IdP's role/group mappings.</param>
/// <param name="AllowProvisioning">Whether an identity with no FHIRBridge account may be auto-created.</param>
/// <param name="Mode">How a difference between the IdP's role and the user's Segue role is handled.</param>
/// <param name="ConflictMode">How several different mapped roles are handled. <paramref name="RoleNames"/> is in mapping order.</param>
/// <param name="NoRoleMode">How a user whose groups map to no Segue role is handled.</param>
public sealed record ExternalRoleResolution(
    bool Succeeded,
    IReadOnlyList<string> RoleNames,
    bool AllowProvisioning,
    ExternalRoleSyncMode Mode = ExternalRoleSyncMode.AutoSwitch,
    ExternalRoleConflictMode ConflictMode = ExternalRoleConflictMode.Deny,
    ExternalNoRoleMode NoRoleMode = ExternalNoRoleMode.Deny)
{
    public static ExternalRoleResolution Failed { get; } = new(false, [], false);

    /// <summary>A failed lookup that still carries the configured mode (a strict mode must not fall open on an outage).</summary>
    public static ExternalRoleResolution FailedFor(ExternalRoleSyncMode mode) => new(false, [], false, mode);
}
