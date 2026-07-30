using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Infrastructure.Persistence;

/// <summary>Used when no database connection string is configured (dev only).</summary>
public sealed class InMemoryNotificationSettingsRepository : INotificationSettingsRepository
{
    private NotificationSettings? _settings;

    public Task<NotificationSettings?> GetAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_settings);

    public Task SaveAsync(NotificationSettings settings, CancellationToken cancellationToken)
    {
        _settings = settings;
        return Task.CompletedTask;
    }
}
