using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Abstractions.Notifications;

/// <summary>
/// Reads/writes the global outbound email (SMTP) configuration — the DB-backed replacement for the "Email" section
/// in appsettings.json. Admin-editable from the Settings hub; <see cref="IEmailSender"/> resolves the same stored
/// settings at send time.
/// </summary>
public interface INotificationSettingsService
{
    Task<NotificationSettingsDto> GetAsync(CancellationToken cancellationToken);

    Task<NotificationSettingsDto> UpdateAsync(UpdateNotificationSettingsRequest request, CancellationToken cancellationToken);
}
