namespace FHIRBridge.Application.Abstractions.Security;

/// <summary>
/// What to do when the role an external identity provider maps to differs from the role the user holds in Segue.
/// </summary>
public enum ExternalRoleSyncMode
{
    /// <summary>The IdP's role wins: the user's Segue role is switched to it at sign-in (and the change is recorded).</summary>
    AutoSwitch = 0,

    /// <summary>The user signs in with the role they already hold in Segue, even if the IdP maps a different one.</summary>
    AllowDifferent = 1,

    /// <summary>Sign-in is refused when the role held in Segue differs from the one the IdP maps.</summary>
    RestrictMismatch = 2
}
