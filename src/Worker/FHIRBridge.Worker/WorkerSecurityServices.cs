using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Worker;

/// <summary>
/// Background-process identity. The Worker has no HTTP user, so it runs as a non-interactive system principal;
/// this satisfies the services registered by the shared infrastructure that expect a current user.
/// </summary>
public sealed class SystemCurrentUserService : ICurrentUserService
{
    public CurrentUserInfo CurrentUser { get; } = new(
        ExternalUserId: "system:worker",
        Email: null,
        DisplayName: "FHIRBridge Worker",
        Roles: [],
        IsAuthenticated: false);
}

/// <summary>
/// The Worker never issues access tokens (that is an API concern). Registered only so the shared DI graph validates;
/// invoking it is a programming error.
/// </summary>
public sealed class WorkerAccessTokenIssuer : IAccessTokenIssuer
{
    public AccessTokenDto Issue(
        User user,
        IReadOnlyCollection<string> roleNames)
        => throw new NotSupportedException("Access tokens are not issued by the background worker.");

    public (string TokenHash, DateTime ExpiresOnUtc) IssueRefreshToken()
        => throw new NotSupportedException("Refresh tokens are not issued by the background worker.");
}
