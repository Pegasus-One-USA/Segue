namespace FHIRBridge.Application.Abstractions.Security;

/// <summary>
/// What to do when NONE of a user's IdP groups map to a Segue role.
/// </summary>
public enum ExternalNoRoleMode
{
    /// <summary>Refuse the sign-in: the user has no role from the identity provider.</summary>
    Deny = 0,

    /// <summary>An existing user signs in with the role they already hold in Segue (their role is left alone).</summary>
    AllowSegueRole = 1
}
