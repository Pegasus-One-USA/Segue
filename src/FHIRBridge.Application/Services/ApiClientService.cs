using System.Security.Cryptography;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Mappings;
using FHIRBridge.Domain.Entities;
using FHIRBridge.SharedKernel.Exceptions;

namespace FHIRBridge.Application.Services;

public sealed class ApiClientService : IApiClientService
{
    private const string ClientIdPrefix = "cid_";
    private const int ClientIdRandomBytes = 18;
    private const int ClientSecretRandomBytes = 32;

    private readonly IApiClientRepository _repository;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IUserDisplayNameResolver _userDisplayNameResolver;

    public ApiClientService(
        IApiClientRepository repository,
        IPasswordHasher passwordHasher,
        IUserDisplayNameResolver userDisplayNameResolver)
    {
        _repository = repository;
        _passwordHasher = passwordHasher;
        _userDisplayNameResolver = userDisplayNameResolver;
    }

    public async Task<PagedResult<ApiClientDto>> GetPagedAsync(
        string? search, int page, int pageSize, CancellationToken cancellationToken)
    {
        var paged = await _repository.GetPagedAsync(search, page, pageSize, cancellationToken);
        var dtos = paged.Items.Select(ApiClientMapper.ToDto).ToArray();

        var names = await _userDisplayNameResolver.ResolveAsync(dtos.Select(dto => dto.CreatedBy), cancellationToken);
        var resolved = dtos.Select(dto => dto with
        {
            CreatedBy = dto.CreatedBy is { } createdBy ? names.GetValueOrDefault(createdBy, createdBy) : null,
        }).ToArray();

        return new PagedResult<ApiClientDto>(resolved, paged.TotalCount, paged.Page, paged.PageSize);
    }

    public async Task<ApiClientCredentialDto> CreateAsync(CreateApiClientRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new InvalidOperationException("Name is required.");
        }

        var clientId = GenerateClientId();
        var plaintextSecret = GenerateSecret();
        var client = new ApiClient(request.Name.Trim(), clientId, _passwordHasher.Hash(plaintextSecret));

        await _repository.AddAsync(client, cancellationToken);

        return new ApiClientCredentialDto(ApiClientMapper.ToDto(client), plaintextSecret);
    }

    public async Task<ApiClientCredentialDto> RegenerateSecretAsync(Guid id, CancellationToken cancellationToken)
    {
        var client = await GetOrThrowAsync(id, cancellationToken);

        var plaintextSecret = GenerateSecret();
        client.RegenerateSecret(_passwordHasher.Hash(plaintextSecret));
        await _repository.UpdateAsync(client, cancellationToken);

        return new ApiClientCredentialDto(ApiClientMapper.ToDto(client), plaintextSecret);
    }

    public async Task<ApiClientDto> UpdateAsync(Guid id, UpdateApiClientRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new InvalidOperationException("Name is required.");
        }

        var client = await GetOrThrowAsync(id, cancellationToken);
        client.Rename(request.Name.Trim());
        client.SetEnabled(request.IsEnabled);
        await _repository.UpdateAsync(client, cancellationToken);

        return ApiClientMapper.ToDto(client);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var client = await GetOrThrowAsync(id, cancellationToken);
        await _repository.DeleteAsync(client, cancellationToken);
    }

    public async Task<ApiClientDto> AddReturnUrlAsync(
        Guid id, AddApiClientReturnUrlRequest request, CancellationToken cancellationToken)
    {
        var client = await GetOrThrowAsync(id, cancellationToken);
        var normalizedUrl = request.MatchMode == ReturnUrlMatchMode.Domain
            ? ValidateAndNormalizeReturnUrlDomain(request.Url)
            : ValidateAndNormalizeReturnUrl(request.Url);

        if (await _repository.ReturnUrlExistsAsync(id, normalizedUrl, cancellationToken))
        {
            throw new InvalidOperationException(request.MatchMode == ReturnUrlMatchMode.Domain
                ? "This domain is already registered for this client."
                : "This return URL is already registered for this client.");
        }

        client.AddReturnUrl(normalizedUrl, request.Label?.Trim(), request.MatchMode);
        await _repository.UpdateAsync(client, cancellationToken);

        return ApiClientMapper.ToDto(client);
    }

    public async Task<ApiClientDto> RemoveReturnUrlAsync(Guid id, Guid returnUrlId, CancellationToken cancellationToken)
    {
        var client = await GetOrThrowAsync(id, cancellationToken);

        if (!client.RemoveReturnUrl(returnUrlId))
        {
            throw new NotFoundException(nameof(ApiClientReturnUrl), returnUrlId);
        }

        await _repository.UpdateAsync(client, cancellationToken);

        return ApiClientMapper.ToDto(client);
    }

    private async Task<ApiClient> GetOrThrowAsync(Guid id, CancellationToken cancellationToken) =>
        await _repository.GetByIdAsync(id, cancellationToken)
            ?? throw new NotFoundException(nameof(ApiClient), id);

    // Same shape as AllowedCorsOriginsService.ValidateAndNormalize, except a return URL is the caller's own
    // page (not a browser origin), so a path is required rather than forbidden, and it's kept in the result.
    private static string ValidateAndNormalizeReturnUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidOperationException("Return URL is required.");
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("Return URL must be an absolute URL, e.g. https://app.example.com/connect/callback.");
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Return URL must use http or https.");
        }

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException("Return URL must not include a query string or fragment.");
        }

        return uri.GetLeftPart(UriPartial.Path);
    }

    // Same absolute-URL/http(s)-only checks as ValidateAndNormalizeReturnUrl, but the caller is expected to
    // provide just an origin (e.g. https://app.example.com) — a path, query string or fragment is REJECTED
    // outright rather than silently stripped, so a user who pastes a full page URL by mistake gets a clear
    // error instead of it quietly being turned into a much broader domain-wide grant than they intended.
    private static string ValidateAndNormalizeReturnUrlDomain(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidOperationException("Domain is required.");
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("Domain must be an absolute URL, e.g. https://app.example.com.");
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Domain must use http or https.");
        }

        if (uri.AbsolutePath is not ("/" or "") || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException(
                "Domain must be just the domain, with no page path, e.g. https://app.example.com — not https://app.example.com/some/page.");
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }

    private static string GenerateClientId() =>
        ClientIdPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(ClientIdRandomBytes))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string GenerateSecret() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(ClientSecretRandomBytes))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
