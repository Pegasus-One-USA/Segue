using FHIRBridge.Application.Abstractions.Audit;
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
    private const string Module = "SystemSettings";

    private readonly IAllowedCorsOriginRepository _repository;
    private readonly IAllowedCorsOriginsCache _cache;
    private readonly IUserActivityAuditService _userActivityAuditService;
    private readonly ICurrentUserService _currentUserService;
    private readonly bool _requireHttps;

    public AllowedCorsOriginsService(
        IAllowedCorsOriginRepository repository,
        IAllowedCorsOriginsCache cache,
        IUserActivityAuditService userActivityAuditService,
        ICurrentUserService currentUserService,
        IOptions<AllowedCorsOriginsOptions> options)
    {
        _repository = repository;
        _cache = cache;
        _userActivityAuditService = userActivityAuditService;
        _currentUserService = currentUserService;
        _requireHttps = options.Value.RequireHttps;
    }

    public async Task<IReadOnlyList<AllowedCorsOriginDto>> GetAllAsync(CancellationToken cancellationToken)
    {
        var origins = await _repository.GetAllAsync(cancellationToken);
        return origins.Select(AllowedCorsOriginMapper.ToDto).ToArray();
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
        await AuditAsync("AllowedCorsOriginAdded", origin.Id, normalizedOrigin, cancellationToken);
        _cache.Invalidate();

        return AllowedCorsOriginMapper.ToDto(origin);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var origin = await _repository.GetByIdAsync(id, cancellationToken)
            ?? throw new NotFoundException(nameof(AllowedCorsOrigin), id);

        await _repository.DeleteAsync(origin, cancellationToken);
        await AuditAsync("AllowedCorsOriginRemoved", origin.Id, origin.OriginUrl, cancellationToken);
        _cache.Invalidate();
    }

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

    private async Task AuditAsync(string action, Guid entityId, string originUrl, CancellationToken cancellationToken)
    {
        var user = _currentUserService.CurrentUser;
        var userId = Guid.TryParse(user.ExternalUserId, out var parsed) ? parsed : (Guid?)null;

        await _userActivityAuditService.RecordAsync(
            new RecordUserActivityRequest(
                UserId: userId,
                UserEmail: user.AuditName,
                Category: UserActivityCategories.Administration,
                Activity: $"{action}: {originUrl}",
                Status: UserActivityStatuses.Success,
                EntityName: nameof(AllowedCorsOrigin),
                EntityId: entityId,
                IpAddress: user.IpAddress,
                UserAgent: user.UserAgent,
                CorrelationId: user.CorrelationId,
                Module: Module,
                Action: action),
            cancellationToken);
    }
}
