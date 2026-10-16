using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Application.Abstractions.Persistence;

public interface ISystemSettingRepository
{
    Task<IReadOnlyList<SystemSetting>> GetAllAsync(CancellationToken cancellationToken);

    Task<SystemSetting?> GetByKeyAsync(string key, CancellationToken cancellationToken);

    Task<SystemSetting> UpsertAsync(string key, string value, string? description, CancellationToken cancellationToken);

    /// <summary>
    /// Inserts the setting only when no setting with that key exists, atomically across instances (unique key):
    /// returns the stored setting, which is the existing one (or a concurrent winner's) when the key was taken.
    /// Never overwrites.
    /// </summary>
    Task<SystemSetting> AddIfAbsentAsync(string key, string value, string? description, CancellationToken cancellationToken);

    Task DeleteAsync(string key, CancellationToken cancellationToken);
}
