using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Persistence;

/// <summary>
/// EF Core implementation of <see cref="INotificationSettingsRepository"/>. There is at most one row — SaveAsync
/// inserts it on first save and updates it on every save after, so callers never need to distinguish create/update.
/// </summary>
public sealed class EfNotificationSettingsRepository : INotificationSettingsRepository
{
    private readonly FHIRBridgeDbContext _db;

    public EfNotificationSettingsRepository(FHIRBridgeDbContext db)
    {
        _db = db;
    }

    public Task<NotificationSettings?> GetAsync(CancellationToken cancellationToken) =>
        _db.NotificationSettings.FirstOrDefaultAsync(cancellationToken);

    public async Task SaveAsync(NotificationSettings settings, CancellationToken cancellationToken)
    {
        var existing = await _db.NotificationSettings.FirstOrDefaultAsync(cancellationToken);
        if (existing is null)
        {
            _db.NotificationSettings.Add(settings);
        }
        else if (!ReferenceEquals(existing, settings))
        {
            existing.Update(
                settings.IsEnabled,
                settings.Host,
                settings.Port,
                settings.EnableSsl,
                settings.Username,
                settings.FromAddress,
                settings.FromName,
                settings.PasswordSecretReference);
        }

        await _db.SaveChangesAsync(cancellationToken);
    }
}
