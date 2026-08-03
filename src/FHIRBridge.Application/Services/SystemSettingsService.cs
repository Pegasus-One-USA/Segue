using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Mappings;

namespace FHIRBridge.Application.Services;

public sealed class SystemSettingsService : ISystemSettingsService
{
    private readonly ISystemSettingRepository _repository;
    private readonly ISystemSettingsCache _cache;
    private readonly IUserDisplayNameResolver _userDisplayNameResolver;

    public SystemSettingsService(
        ISystemSettingRepository repository, ISystemSettingsCache cache, IUserDisplayNameResolver userDisplayNameResolver)
    {
        _repository = repository;
        _cache = cache;
        _userDisplayNameResolver = userDisplayNameResolver;
    }

    public async Task<IReadOnlyList<SystemSettingDto>> GetAllAsync(CancellationToken cancellationToken)
    {
        var settings = await _repository.GetAllAsync(cancellationToken);
        var dtos = settings.Select(SystemSettingMapper.ToDto).ToArray();

        var names = await _userDisplayNameResolver.ResolveAsync(
            dtos.SelectMany(dto => new[] { dto.CreatedBy, dto.ModifiedBy }), cancellationToken);

        return dtos.Select(dto => dto with
        {
            CreatedBy = dto.CreatedBy is { } createdBy ? names.GetValueOrDefault(createdBy, createdBy) : null,
            ModifiedBy = dto.ModifiedBy is { } modifiedBy ? names.GetValueOrDefault(modifiedBy, modifiedBy) : null,
        }).ToArray();
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
