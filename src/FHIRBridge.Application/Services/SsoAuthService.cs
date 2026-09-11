using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Governance;

using FHIRBridge.Observability.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FHIRBridge.Application.Services;

public sealed class SsoAuthService : ISsoAuthService
{
    private readonly IExternalTokenValidator _tokenValidator;
    private readonly IUserAccessRepository _repository;
    private readonly ILocalAuthService _localAuth;
    private readonly IGovernanceLogger _governanceLogger;

    public SsoAuthService(
        IExternalTokenValidator tokenValidator,
        IUserAccessRepository repository,
        ILocalAuthService localAuth,
        IGovernanceLogger governanceLogger,
        ILogger<SsoAuthService>? logger = null)
    {
        _tokenValidator = tokenValidator;
        _repository = repository;
        _localAuth = localAuth;
        _governanceLogger = governanceLogger;
        _logger = logger ?? NullLogger<SsoAuthService>.Instance;
    }

    private readonly ILogger<SsoAuthService> _logger;

    public async Task<LocalLoginResponse> LoginAsync(SsoLoginRequest request, CancellationToken cancellationToken)
    {
        var authenticationType = $"SSO:{request.Provider}";

        ExternalIdentity identity;
        try
        {
            identity = await _tokenValidator.ValidateAsync(request.Provider, request.Token, cancellationToken);
        }
        catch (Exception exception)
        {
            // Token rejected before we know who it claims to be — no user identity to attach yet, but the
            // attempt itself (provider, failure reason, IP/correlation via ambient context) is still logged.
            await _governanceLogger.LogAuthenticationAsync(
                new AuthenticationEntry(authenticationType, Success: false, FailureReason: exception.Message),
                cancellationToken);

            // No user identity to attach yet -- the token itself was rejected. Misconfigured issuer/audience/
            // signing keys all surface here, and they look identical to the end user ("login failed").
            _logger.LogWarning(
                LogEvents.LoginFailed,
                "SSO token validation failed for {AuthenticationType}: {LoginFailureReason}",
                authenticationType, exception.Message);

            throw;
        }

        return await LoginWithIdentityAsync(identity, cancellationToken);
    }

    public async Task<LocalLoginResponse> LoginWithIdentityAsync(ExternalIdentity identity, CancellationToken cancellationToken)
    {
        var authenticationType = $"SSO:{identity.Provider}";

        // Prefer the stable external subject; fall back to the verified email for first-time SSO of a
        // user that was provisioned locally (e.g. invited) but not yet linked to this identity.
        var user = await _repository.GetUserByExternalIdAsync(identity.Subject, cancellationToken);
        if (user is null && !string.IsNullOrWhiteSpace(identity.Email))
        {
            user = await _repository.GetUserByEmailAsync(identity.Email.Trim().ToLowerInvariant(), cancellationToken);
        }

        if (user is null || !user.IsEnabled)
        {
            await _governanceLogger.LogAuthenticationAsync(
                new AuthenticationEntry(
                    authenticationType, Success: false, identity.Email, "No enabled account is linked to this identity."),
                cancellationToken);

            // The identity is genuine; it just has nowhere to land. Distinct from a rejected token above, and the
            // distinction is the whole answer to "SSO works for my colleague but not me". Subject is the IdP's
            // opaque user id, not an address -- the address is on the governance entry above.
            _logger.LogWarning(
                LogEvents.LoginFailed,
                "SSO login failed for {AuthenticationType} subject {ExternalSubject}: {LoginFailureReason}",
                authenticationType, identity.Subject,
                user is null ? "no account is linked to this external identity" : "the linked account is disabled");

            // 401 — no enabled account matches this external identity.
            throw new UnauthorizedAccessException("No enabled Segue account is linked to this identity.");
        }

        // Ensure the identity is linked so subsequent logins resolve by subject.
        if (!string.Equals(user.ExternalUserId, identity.Subject, StringComparison.Ordinal) ||
            user.LoginProvider != identity.Provider ||
            user.IsLocalLoginEnabled)
        {
            user.LinkExternalIdentity(identity.Subject, identity.Provider);
            await _repository.UpdateUserAsync(user, cancellationToken);
        }

        var response = await _localAuth.IssueSessionAsync(user, cancellationToken);

        await _governanceLogger.LogAuthenticationAsync(
            new AuthenticationEntry(authenticationType, Success: true, user.Email),
            cancellationToken);

        return response;
    }
}
