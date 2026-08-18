namespace FHIRBridge.Domain.Enums;

/// <summary>
/// How a user authenticates. <see cref="Local"/> is FHIRBridge's own password path; the others are
/// external identity providers whose tokens are exchanged for a FHIRBridge-minted local JWT.
/// </summary>
public enum LoginProvider
{
    Local = 0,
    Entra = 1,
    Google = 2,
    Saml = 3
}
