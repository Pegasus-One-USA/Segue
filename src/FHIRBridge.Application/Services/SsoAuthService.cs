using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Application.Services;

public sealed class SsoAuthService : ISsoAuthService
{
    private readonly IExternalTokenValidator _tokenValidator;
    private readonly IUserAccessRepository _repository;
    private readonly ILocalAuthService _localAuth;

    public SsoAuthService(
        IExternalTokenValidator tokenValidator,
        IUserAccessRepository repository,
        ILocalAuthService localAuth)
    {
        _tokenValidator = tokenValidator;
        _repository = repository;
        _localAuth = localAuth;
    }

    public async Task<LocalLoginResponse> LoginAsync(SsoLoginRequest request, CancellationToken cancellationToken)
    {
        var identity = await _tokenValidator.ValidateAsync(request.Provider, request.Token, cancellationToken);

        // Prefer the stable external subject; fall back to the verified email for first-time SSO of a
        // user that was provisioned locally (e.g. invited) but not yet linked to this identity.
        var user = await _repository.GetUserByExternalIdAsync(identity.Subject, cancellationToken);
        if (user is null && !string.IsNullOrWhiteSpace(identity.Email))
        {
            user = await _repository.GetUserByEmailAsync(identity.Email.Trim().ToLowerInvariant(), cancellationToken);
        }

        if (user is null || !user.IsEnabled)
        {
            // 401 — no enabled account matches this external identity.
            throw new UnauthorizedAccessException("No enabled FHIRBridge account is linked to this identity.");
        }

        // Ensure the identity is linked so subsequent logins resolve by subject.
        if (!string.Equals(user.ExternalUserId, identity.Subject, StringComparison.Ordinal) ||
            user.LoginProvider != request.Provider ||
            user.IsLocalLoginEnabled)
        {
            user.LinkExternalIdentity(identity.Subject, request.Provider);
            await _repository.UpdateUserAsync(user, cancellationToken);
        }

        return await _localAuth.IssueSessionAsync(user, cancellationToken);
    }
}
