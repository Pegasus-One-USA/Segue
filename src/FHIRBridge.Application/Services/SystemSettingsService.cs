using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Mappings;

namespace FHIRBridge.Application.Services;

public sealed class SystemSettingsService : ISystemSettingsService
{
    private readonly ISystemSettingRepository _repository;
    private readonly ISystemSettingsCache _cache;

    public SystemSettingsService(ISystemSettingRepository repository, ISystemSettingsCache cache)
    {
        _repository = repository;
        _cache = cache;
    }

    public async Task<IReadOnlyList<SystemSettingDto>> GetAllAsync(CancellationToken cancellationToken)
    {
        var settings = await _repository.GetAllAsync(cancellationToken);
        return settings.Select(SystemSettingMapper.ToDto).ToArray();
    }

    public async Task<SystemSettingDto> SetAsync(
        string key, string value, string? description, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException("Setting key is required.");
        }

        var setting = await _repository.UpsertAsync(key.Trim(), value, description, cancellationToken);
        _cache.Invalidate();

        return SystemSettingMapper.ToDto(setting);
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        await _repository.DeleteAsync(key, cancellationToken);
        _cache.Invalidate();
    }
}
