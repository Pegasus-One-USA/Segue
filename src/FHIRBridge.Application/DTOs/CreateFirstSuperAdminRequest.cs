namespace FHIRBridge.Application.DTOs;

/// <summary>
/// First-run request to create the sole SuperAdmin (local/password path). Only honored when no user exists.
/// Bundles the outbound SMTP configuration and Terms &amp; Conditions acceptance so a fresh deployment leaves
/// setup with email already enabled — there is no separate "configure email first" step.
/// </summary>
public sealed record CreateFirstSuperAdminRequest(
    string Email,
    string? DisplayName,
    string Password,
    bool AcceptTerms,
    FirstRunEmailSettingsRequest EmailSettings,
    string? FirstName = null,
    string? LastName = null);

/// <summary>
/// The SMTP settings collected on the first-run screen. Saved via <see cref="Abstractions.Notifications.INotificationSettingsService"/>
/// with <c>IsEnabled</c> forced to true — email is always enabled once the first admin is created.
/// </summary>
public sealed record FirstRunEmailSettingsRequest(
    string Host,
    int Port,
    bool EnableSsl,
    string? Username,
    string? Password,
    string FromAddress,
    string FromName);

/// <summary>
/// Reports whether the deployment still needs its first SuperAdmin created, plus the current license
/// gate state — anonymous and polled at portal boot (and again after login/license-apply) so the portal
/// can show its "license not active" header banner without needing an authenticated, admin-only call.
/// </summary>
public sealed record SetupStatusDto(bool RequiresSetup, bool LicenseActive, string LicenseState);
