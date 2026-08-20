using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.DTOs;

/// <summary>Exchange an external IdP token for a FHIRBridge session (sign-in of an existing linked/known user).</summary>
public sealed record SsoLoginRequest(
    LoginProvider Provider,
    string Token);

/// <summary>Accept a pending invitation using an external IdP identity instead of setting a password.</summary>
public sealed record AcceptInviteSsoRequest(
    string Email,
    string InvitationToken,
    LoginProvider Provider,
    string Token);

/// <summary>First-run creation of the sole SuperAdmin via an external IdP identity (no password).</summary>
public sealed record CreateFirstSuperAdminSsoRequest(
    LoginProvider Provider,
    string Token);

/// <summary>Public SSO configuration for the portal: which providers are enabled and their client-side settings.</summary>
public sealed record SsoConfigDto(
    SsoEntraConfigDto Entra,
    SsoGoogleConfigDto Google,
    SsoSamlConfigDto Saml,
    SsoMagicLinkConfigDto MagicLink);

public sealed record SsoEntraConfigDto(
    bool Enabled,
    string? Authority,
    string? ClientId);

public sealed record SsoGoogleConfigDto(
    bool Enabled,
    string? ClientId);

/// <summary>SAML is a plain full-page redirect (no client-side SDK/token needed), unlike Entra/Google's
/// popup + token exchange — the portal only needs to know whether to show the button.</summary>
public sealed record SsoSamlConfigDto(bool Enabled);

public sealed record SsoMagicLinkConfigDto(bool Enabled);
