namespace FHIRBridge.Application.DTOs;

/// <summary>
/// The global outbound email/SMTP configuration. Never carries the password — only whether one is currently
/// configured, so the portal form can show "a password is set" without ever round-tripping the secret value.
/// </summary>
public sealed record NotificationSettingsDto(
    bool IsEnabled,
    string Host,
    int Port,
    bool EnableSsl,
    string Username,
    string FromAddress,
    string FromName,
    bool HasPasswordConfigured);
