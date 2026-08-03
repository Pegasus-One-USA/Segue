using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Services;

/// <summary>Admin-facing CRUD over system settings; used by the API. Read-heavy runtime consumers
/// (background services, middleware) should depend on ISystemSettingsCache directly instead.</summary>
public interface ISystemSettingsService
{
    Task<IReadOnlyList<SystemSettingDto>> GetAllAsync(CancellationToken cancellationToken);

    Task<SystemSettingDto> SetAsync(string key, string value, string? description, CancellationToken cancellationToken);

    Task DeleteAsync(string key, CancellationToken cancellationToken);
}
