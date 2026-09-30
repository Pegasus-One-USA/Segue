using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Application.DTOs;

public sealed record ApiClientReturnUrlDto(Guid Id, string Url, string? Label, ReturnUrlMatchMode MatchMode);

public sealed record ApiClientDto(
    Guid Id,
    string Name,
    string ClientId,
    bool IsEnabled,
    DateTime? LastUsedOnUtc,
    IReadOnlyList<ApiClientReturnUrlDto> ReturnUrls,
    DateTime CreatedOnUtc,
    string? CreatedBy);

/// <summary>Returned only from create/regenerate — the one and only time the plaintext secret is ever sent.</summary>
public sealed record ApiClientCredentialDto(ApiClientDto Client, string PlaintextSecret);

public sealed record CreateApiClientRequest(string Name);

public sealed record UpdateApiClientRequest(string Name, bool IsEnabled);

public sealed record AddApiClientReturnUrlRequest(string Url, string? Label, ReturnUrlMatchMode MatchMode = ReturnUrlMatchMode.Exact);
