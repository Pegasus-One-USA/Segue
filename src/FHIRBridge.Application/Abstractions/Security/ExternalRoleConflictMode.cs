namespace FHIRBridge.Application.Abstractions.Security;

/// <summary>
/// What to do when a user's IdP groups map to MORE THAN ONE distinct Segue role (a Segue user holds one role).
/// </summary>
public enum ExternalRoleConflictMode
{
    /// <summary>Refuse the sign-in: the mapping is ambiguous.</summary>
    Deny = 0,

    /// <summary>The first matching row in the admin-ordered mapping list decides the role.</summary>
    UseMappingOrder = 1
}
