namespace FHIRBridge.Application.DTOs;

public sealed record UpdateNotificationSettingsRequest(
    bool IsEnabled,
    string Host,
    int Port,
    bool EnableSsl,
    string Username,
    string FromAddress,
    string FromName,
    // Write-only: when non-null/non-empty, replaces the stored password. Null/blank preserves whatever password
    // is already saved — the GET response never returns the password, so the portal form only sends this when the
    // admin actually typed a new one.
    string? Password);
