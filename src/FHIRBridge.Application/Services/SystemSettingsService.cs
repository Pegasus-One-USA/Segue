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

        var normalizedKey = key.Trim();
        var normalizedValue = ValidatedValueFor(normalizedKey, value);

        var setting = await _repository.UpsertAsync(normalizedKey, normalizedValue, description, cancellationToken);
        _cache.Invalidate();

        return SystemSettingMapper.ToDto(setting);
    }

    // The generic key/value SystemSettings screen has no per-key form validation the way a dedicated screen
    // (e.g. Allowed Origins) does — a handful of keys carry enough blast radius on a bad value (this one breaks
    // every EHR OAuth integration silently) that letting a typo save unnoticed is worse than a rejected save.
    // A switch on the setting KEY (a plain string, not ApplicationType) isn't the pattern the architecture tests
    // ban — this only grows if a future setting needs the same treatment.
    private static string ValidatedValueFor(string key, string value) => key switch
    {
        "OAuth:PublicBaseUrl" => ValidatePublicBaseUrl(value),
        _ => value,
    };

    // Blank is valid and means "derive it from the incoming request instead" (OAuthPublicOriginResolver) — only
    // a non-blank value is held to the same "scheme + host + port, no path/query/fragment" shape
    // AllowedCorsOriginsService.ValidateAndNormalize enforces for exactly the same reason: this value is
    // concatenated straight into a URL registered with an EHR, so a path/query/trailing-slash typo here would
    // otherwise silently break every OAuth redirect instead of failing the save.
    private static string ValidatePublicBaseUrl(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return trimmed;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException(
                "OAuth:PublicBaseUrl must be an absolute URL, e.g. https://portal.example.com.");
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("OAuth:PublicBaseUrl must use http or https.");
        }

        if (uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException(
                "OAuth:PublicBaseUrl must not include a path, query, or fragment — scheme and host only, e.g. https://portal.example.com.");
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        await _repository.DeleteAsync(key, cancellationToken);
        _cache.Invalidate();
    }
}
