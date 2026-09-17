using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Mappings;
using FHIRBridge.Domain.Entities;
using FHIRBridge.SharedKernel.Exceptions;
using Microsoft.Extensions.Options;

namespace FHIRBridge.Application.Services;

public sealed class AllowedCorsOriginsService : IAllowedCorsOriginsService
{
    private readonly IAllowedCorsOriginRepository _repository;
    private readonly IAllowedCorsOriginsCache _cache;
    private readonly IUserDisplayNameResolver _userDisplayNameResolver;
    private readonly bool _requireHttps;

    public AllowedCorsOriginsService(
        IAllowedCorsOriginRepository repository,
        IAllowedCorsOriginsCache cache,
        IUserDisplayNameResolver userDisplayNameResolver,
        IOptions<AllowedCorsOriginsOptions> options)
    {
        _repository = repository;
        _cache = cache;
        _userDisplayNameResolver = userDisplayNameResolver;
        _requireHttps = options.Value.RequireHttps;
    }

    public async Task<IReadOnlyList<AllowedCorsOriginDto>> GetAllAsync(CancellationToken cancellationToken)
    {
        var origins = await _repository.GetAllAsync(cancellationToken);
        var dtos = origins.Select(AllowedCorsOriginMapper.ToDto).ToArray();

        var names = await _userDisplayNameResolver.ResolveAsync(
            dtos.Select(dto => dto.CreatedBy), cancellationToken);

        return dtos.Select(dto => dto with
        {
            CreatedBy = dto.CreatedBy is { } createdBy ? names.GetValueOrDefault(createdBy, createdBy) : null,
        }).ToArray();
    }

    public async Task<AllowedCorsOriginDto> AddAsync(
        CreateAllowedCorsOriginRequest request, CancellationToken cancellationToken)
    {
        var normalizedOrigin = ValidateAndNormalize(request.OriginUrl);

        if (await _repository.ExistsAsync(normalizedOrigin, cancellationToken))
        {
            throw new InvalidOperationException("This origin is already allowed.");
        }

        var origin = new AllowedCorsOrigin(normalizedOrigin, request.Label?.Trim());
        await _repository.AddAsync(origin, cancellationToken);
        _cache.Invalidate();

        var dto = AllowedCorsOriginMapper.ToDto(origin);
        var name = await _userDisplayNameResolver.ResolveOneAsync(dto.CreatedBy, cancellationToken);
        return dto with { CreatedBy = name };
    }

    public async Task<AllowedCorsOriginDto> UpdateAsync(
        Guid id, UpdateAllowedCorsOriginRequest request, CancellationToken cancellationToken)
    {
        var origin = await _repository.GetByIdAsync(id, cancellationToken)
            ?? throw new NotFoundException(nameof(AllowedCorsOrigin), id);

        var normalizedOrigin = ValidateAndNormalize(request.OriginUrl);

        if (!string.Equals(normalizedOrigin, origin.OriginUrl, StringComparison.OrdinalIgnoreCase)
            && await _repository.ExistsAsync(normalizedOrigin, cancellationToken))
        {
            throw new InvalidOperationException("This origin is already allowed.");
        }

        origin.Update(normalizedOrigin, request.Label?.Trim());
        await _repository.UpdateAsync(origin, cancellationToken);
        _cache.Invalidate();

        var dto = AllowedCorsOriginMapper.ToDto(origin);
        var name = await _userDisplayNameResolver.ResolveOneAsync(dto.CreatedBy, cancellationToken);
        return dto with { CreatedBy = name };
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var origin = await _repository.GetByIdAsync(id, cancellationToken)
            ?? throw new NotFoundException(nameof(AllowedCorsOrigin), id);

        await _repository.DeleteAsync(origin, cancellationToken);
        _cache.Invalidate();
    }

    public void Reload() => _cache.Invalidate();

    // Origin only — scheme + host + port, no path/query/fragment — so a value here can only ever widen the
    // browser-level CORS allow-list, never smuggle in a path-scoped or query-scoped exception.
    private string ValidateAndNormalize(string originUrl)
    {
        if (string.IsNullOrWhiteSpace(originUrl))
        {
            throw new InvalidOperationException("Origin URL is required.");
        }

        if (!Uri.TryCreate(originUrl.Trim(), UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("Origin URL must be an absolute URL, e.g. https://portal.example.com.");
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Origin URL must use http or https.");
        }

        if (_requireHttps && uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Origin URL must use https.");
        }

        if (uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException(
                "Origin URL must not include a path, query, or fragment — scheme and host only, e.g. https://portal.example.com.");
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }
}
