using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Application.Abstractions.Persistence;

public interface ISystemSettingRepository
{
    Task<IReadOnlyList<SystemSetting>> GetAllAsync(CancellationToken cancellationToken);

    Task<SystemSetting?> GetByKeyAsync(string key, CancellationToken cancellationToken);

    Task<SystemSetting> UpsertAsync(string key, string value, string? description, CancellationToken cancellationToken);

    Task DeleteAsync(string key, CancellationToken cancellationToken);
}
