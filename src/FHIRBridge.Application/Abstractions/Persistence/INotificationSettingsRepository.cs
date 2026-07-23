using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Application.Abstractions.Persistence;

/// <summary>
/// Persistence for the single, global <see cref="NotificationSettings"/> row. Unlike the tenant-config entities on
/// <see cref="IConfigurationRepository"/>, there is exactly one (or zero, before first save) row — no id-scoped
/// lookups, listing, or paging.
/// </summary>
public interface INotificationSettingsRepository
{
    /// <summary>Returns the settings row, or null if the admin has never saved one (email stays disabled).</summary>
    Task<NotificationSettings?> GetAsync(CancellationToken cancellationToken);

    /// <summary>Inserts the row on first save; updates the existing one on every save after.</summary>
    Task SaveAsync(NotificationSettings settings, CancellationToken cancellationToken);
}
