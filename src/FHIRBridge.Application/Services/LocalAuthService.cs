using System.Security.Cryptography;
using FHIRBridge.Application.Abstractions.Notifications;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Governance;
using Microsoft.Extensions.Options;

namespace FHIRBridge.Application.Services;

public sealed class LocalAuthService : ILocalAuthService
{
    private readonly IUserAccessRepository _repository;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IAccessTokenIssuer _accessTokenIssuer;
    private readonly ICurrentUserService _currentUserService;
    private readonly IEmailSender _emailSender;
    private readonly ITotpService _totpService;
    private readonly IGovernanceLogger _governanceLogger;
    private readonly LocalAuthOptions _localAuthOptions;

    public LocalAuthService(
        IUserAccessRepository repository,
        IPasswordHasher passwordHasher,
        IAccessTokenIssuer accessTokenIssuer,
        ICurrentUserService currentUserService,
        IEmailSender emailSender,
        ITotpService totpService,
        IGovernanceLogger governanceLogger,
        IOptions<LocalAuthOptions> localAuthOptions)
    {
        _repository = repository;
        _passwordHasher = passwordHasher;
        _accessTokenIssuer = accessTokenIssuer;
        _currentUserService = currentUserService;
        _emailSender = emailSender;
        _totpService = totpService;
        _governanceLogger = governanceLogger;
        _localAuthOptions = localAuthOptions.Value;
    }

    public async Task<LocalLoginResponse> LoginAsync(
        LocalLoginRequest request,
        CancellationToken cancellationToken)
    {
        var email = NormalizeEmail(request.Email);
        var user = await _repository.GetUserByEmailAsync(email, cancellationToken);

        // Reject a locked-out account before touching the password so lockout can't be bypassed and
        // repeated attempts don't extend the window silently.
        if (user is not null && user.IsLockedOut(DateTime.UtcNow))
        {
            await _governanceLogger.LogAuthenticationAsync(
                new AuthenticationEntry("Local", Success: false, email, "Account is locked out"), cancellationToken);
            await _governanceLogger.LogSecurityEventAsync(
                new SecurityEventEntry("LoginAttemptWhileLocked", "Medium", email), cancellationToken);
            throw new InvalidOperationException("Account is temporarily locked due to too many failed login attempts. Try again later.");
        }

        var accountUsable = user is not null &&
                            user.IsEnabled &&
                            user.IsLocalLoginEnabled &&
                            !string.IsNullOrWhiteSpace(user.PasswordHash);

        if (!accountUsable || !_passwordHasher.Verify(request.Password, user!.PasswordHash!))
        {
            // Count the failed attempt against a real, local-login account so the lockout threshold engages.
            if (user is not null && accountUsable)
            {
                user.RegisterFailedLogin(_localAuthOptions.Lockout.MaxFailedAttempts,
                    TimeSpan.FromMinutes(_localAuthOptions.Lockout.LockoutMinutes));
                await _repository.UpdateUserAsync(user, cancellationToken);

                if (user.IsLockedOut(DateTime.UtcNow))
                {
                    await _governanceLogger.LogSecurityEventAsync(
                        new SecurityEventEntry("AccountLockedThresholdReached", "High", email,
                            $"{_localAuthOptions.Lockout.MaxFailedAttempts} consecutive failed login attempts"),
                        cancellationToken);
                }
            }

            await _governanceLogger.LogAuthenticationAsync(
                new AuthenticationEntry("Local", Success: false, email, "Invalid email or password"), cancellationToken);
            throw new InvalidOperationException("Invalid email or password.");
        }

        // Second factor: when the account has MFA enabled, the password alone isn't enough to log in.
        // Issue a short-lived, single-use challenge and let the client finish via CompleteMfaLoginAsync —
        // no tokens yet, and no lockout penalty just for not having submitted a code.
        if (user.MfaEnabled)
        {
            // Reuse the refresh-token issuer purely for its "random bytes -> SHA-256 hash" mint —
            // the hash itself becomes the opaque, client-facing challenge token (same trick
            // IssueRefreshToken uses), just with a 5-minute expiry instead of its 30-day default.
            var (challengeToken, _) = _accessTokenIssuer.IssueRefreshToken();
            var challengeExpiresOnUtc = DateTime.UtcNow.AddMinutes(5);
            user.SetMfaChallengeToken(challengeToken, challengeExpiresOnUtc);
            await _repository.UpdateUserAsync(user, cancellationToken);

            return LocalLoginResponse.MfaRequired(challengeToken, challengeExpiresOnUtc);
        }

        return await FinishSuccessfulLoginAsync(user, cancellationToken);
    }

    public async Task<LocalLoginResponse> CompleteMfaLoginAsync(
        MfaLoginRequest request,
        CancellationToken cancellationToken)
    {
        // The challenge token the client holds already IS its own hash (see the IssueRefreshToken
        // reuse in LoginAsync) — no re-hashing needed to use it as the lookup key.
        var user = await _repository.GetUserByMfaChallengeTokenHashAsync(request.ChallengeToken, cancellationToken);

        if (user is null || user.MfaChallengeExpiresOnUtc is null || user.MfaChallengeExpiresOnUtc < DateTime.UtcNow)
        {
            throw new InvalidOperationException("Your session has expired. Please log in again.");
        }

        if (user.IsLockedOut(DateTime.UtcNow))
        {
            throw new InvalidOperationException("Account is temporarily locked due to too many failed login attempts. Try again later.");
        }

        // The account may have been disabled (or had local login turned off) in the minutes between
        // the password check and this call — re-verify before minting tokens, same guard LoginAsync
        // applies up front, so a since-disabled account can't ride an already-issued challenge in.
        if (!user.IsEnabled || !user.IsLocalLoginEnabled)
        {
            user.ClearMfaChallengeToken();
            await _repository.UpdateUserAsync(user, cancellationToken);
            throw new InvalidOperationException("This account is disabled.");
        }

        if (!IsMfaSatisfied(user, request.Code))
        {
            // A guess against a live challenge is a real failed attempt, unlike the code-less first step.
            user.RegisterFailedLogin(_localAuthOptions.Lockout.MaxFailedAttempts,
                TimeSpan.FromMinutes(_localAuthOptions.Lockout.LockoutMinutes));
            await _repository.UpdateUserAsync(user, cancellationToken);
            await _governanceLogger.LogAuthenticationAsync(
                new AuthenticationEntry("MFA", Success: false, user.Email, "Invalid MFA code"), cancellationToken);
            throw new InvalidOperationException("A valid MFA code is required.");
        }

        user.ClearMfaChallengeToken();
        return await FinishSuccessfulLoginAsync(user, cancellationToken);
    }

    /// <summary>Shared tail of a successful local login: records the login and issues a session.</summary>
    private async Task<LocalLoginResponse> FinishSuccessfulLoginAsync(User user, CancellationToken cancellationToken)
    {
        user.RecordLogin();
        await _repository.UpdateUserAsync(user, cancellationToken);
        await _governanceLogger.LogAuthenticationAsync(
            new AuthenticationEntry("Local", Success: true, user.Email), cancellationToken);

        return await CreateLoginResponseAsync(user, cancellationToken);
    }

    public async Task<LocalLoginResponse> ChangePasswordAsync(
        ChangePasswordRequest request,
        CancellationToken cancellationToken)
    {
        ValidatePassword(request.NewPassword);

        var externalUserId = _currentUserService.CurrentUser.ExternalUserId;
        if (string.IsNullOrWhiteSpace(externalUserId))
        {
            throw new InvalidOperationException("Authenticated user id claim is missing.");
        }

        var user = await _repository.GetUserByExternalIdAsync(externalUserId, cancellationToken)
            ?? throw new InvalidOperationException("User was not found.");

        if (!user.IsLocalLoginEnabled ||
            string.IsNullOrWhiteSpace(user.PasswordHash) ||
            !_passwordHasher.Verify(request.CurrentPassword, user.PasswordHash))
        {
            throw new InvalidOperationException("Current password is invalid.");
        }

        user.SetPassword(_passwordHasher.Hash(request.NewPassword), mustChangePassword: false);
        await _repository.UpdateUserAsync(user, cancellationToken);

        return await CreateLoginResponseAsync(user, cancellationToken);
    }

    public async Task<ForgotPasswordResponse> ForgotPasswordAsync(
        ForgotPasswordRequest request,
        CancellationToken cancellationToken)
    {
        var email = NormalizeEmail(request.Email);
        var user = await _repository.GetUserByEmailAsync(email, cancellationToken);
        if (user is null || !user.IsLocalLoginEnabled || !user.IsEnabled)
        {
            return new ForgotPasswordResponse(true, null, null);
        }

        var token = GenerateToken();
        var expiresOnUtc = DateTime.UtcNow.AddMinutes(30);
        user.SetPasswordResetToken(_passwordHasher.Hash(token), expiresOnUtc);
        await _repository.UpdateUserAsync(user, cancellationToken);

        await _emailSender.SendAsync(
            email,
            "Reset your FHIRBridge password",
            BuildPasswordResetEmailBody(user.DisplayName, BuildResetLink(email, token), expiresOnUtc),
            cancellationToken);

        return new ForgotPasswordResponse(true, token, expiresOnUtc);
    }

    public async Task ResetPasswordAsync(
        ResetPasswordRequest request,
        CancellationToken cancellationToken)
    {
        ValidatePassword(request.NewPassword);

        var email = NormalizeEmail(request.Email);
        var user = await _repository.GetUserByEmailAsync(email, cancellationToken)
            ?? throw new InvalidOperationException("Reset token is invalid or expired.");

        if (string.IsNullOrWhiteSpace(user.PasswordResetTokenHash) ||
            user.PasswordResetTokenExpiresOnUtc is null ||
            user.PasswordResetTokenExpiresOnUtc < DateTime.UtcNow ||
            !_passwordHasher.Verify(request.ResetToken, user.PasswordResetTokenHash))
        {
            throw new InvalidOperationException("Reset token is invalid or expired.");
        }

        user.SetPassword(_passwordHasher.Hash(request.NewPassword), mustChangePassword: false);
        await _repository.UpdateUserAsync(user, cancellationToken);
    }

    public async Task<LocalLoginResponse> RefreshTokenAsync(
        RefreshTokenRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            throw new InvalidOperationException("Refresh token is required.");
        }

        var tokenHash = ComputeTokenHash(request.RefreshToken);
        var user = await _repository.GetUserByRefreshTokenHashAsync(tokenHash, cancellationToken)
            ?? throw new InvalidOperationException("Refresh token is invalid or expired.");

        if (!user.IsEnabled ||
            user.RefreshTokenExpiresOnUtc is null ||
            user.RefreshTokenExpiresOnUtc < DateTime.UtcNow)
        {
            user.ClearRefreshToken();
            await _repository.UpdateUserAsync(user, cancellationToken);
            throw new InvalidOperationException("Refresh token is invalid or expired.");
        }

        return await CreateLoginResponseAsync(user, cancellationToken);
    }

    public async Task<LocalLoginResponse> IssueSessionAsync(User user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (!user.IsEnabled)
        {
            throw new InvalidOperationException("This account is disabled.");
        }

        user.RecordLogin();
        await _repository.UpdateUserAsync(user, cancellationToken);

        return await CreateLoginResponseAsync(user, cancellationToken);
    }

    public async Task LogoutAsync(CancellationToken cancellationToken)
    {
        var externalUserId = _currentUserService.CurrentUser.ExternalUserId;
        if (string.IsNullOrWhiteSpace(externalUserId))
        {
            return;
        }

        var user = await _repository.GetUserByExternalIdAsync(externalUserId, cancellationToken);
        if (user is null)
        {
            return;
        }

        user.ClearRefreshToken();
        await _repository.UpdateUserAsync(user, cancellationToken);
        await _governanceLogger.LogAuthenticationAsync(
            new AuthenticationEntry("Logout", Success: true, user.Email), cancellationToken);
    }

    private async Task<LocalLoginResponse> CreateLoginResponseAsync(
        User user,
        CancellationToken cancellationToken)
    {
        var roles = await _repository.GetUserRolesAsync(user.Id, cancellationToken);
        var roleNames = roles.Select(x => x.Name).ToArray();

        var permissionCodes = await GetPermissionCodesAsync(user.Id, roles.Select(r => r.Id).ToArray(), cancellationToken);

        var token = _accessTokenIssuer.Issue(user, roleNames, permissionCodes);

        var (refreshHash, refreshExpiry) = _accessTokenIssuer.IssueRefreshToken();
        user.SetRefreshToken(refreshHash, refreshExpiry);
        await _repository.UpdateUserAsync(user, cancellationToken);

        return new LocalLoginResponse(
            RequiresMfa: false,
            MfaChallengeToken: null,
            MfaChallengeExpiresOnUtc: null,
            AccessToken: token.AccessToken,
            TokenType: token.TokenType,
            ExpiresOnUtc: token.ExpiresOnUtc,
            RequiresPasswordChange: user.MustChangePassword,
            Profile: new UserProfileDto(
                user.Id,
                user.ExternalUserId,
                user.Email,
                user.DisplayName,
                roleNames),
            RefreshToken: BuildRawRefreshToken(refreshHash),
            RefreshTokenExpiresOnUtc: refreshExpiry,
            RequiresMfaSetup: user.IsMfaSetupRequired);
    }

    /// <summary>
    /// Effective permission codes for the user: the union of their role grants, with any direct
    /// per-user <see cref="Domain.Entities.PermissionAllocation"/> overriding the role-derived result
    /// for that specific code — enabled overrides add it even if no role grants it, disabled overrides
    /// remove it even if a role does grant it.
    /// </summary>
    private async Task<string[]> GetPermissionCodesAsync(
        Guid userId,
        IReadOnlyCollection<Guid> roleIds,
        CancellationToken cancellationToken)
    {
        var roleCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var roleId in roleIds)
        {
            var perms = await _repository.GetRolePermissionsAsync(roleId, cancellationToken);
            foreach (var p in perms) roleCodes.Add(p.Name);
        }

        var userAllocations = await _repository.GetUserPermissionAllocationsAsync(userId, cancellationToken);
        var overridden = new HashSet<string>(
            userAllocations.Select(a => a.Permission.Name), StringComparer.OrdinalIgnoreCase);

        var result = new HashSet<string>(
            roleCodes.Where(code => !overridden.Contains(code)), StringComparer.OrdinalIgnoreCase);

        foreach (var allocation in userAllocations)
        {
            if (allocation.IsEnabled)
            {
                result.Add(allocation.Permission.Name);
            }
        }

        return [..result];
    }

    private static string NormalizeEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            throw new InvalidOperationException("Email is required.");
        }

        return email.Trim().ToLowerInvariant();
    }

    private string BuildResetLink(string email, string token)
    {
        var template = _localAuthOptions.PasswordResetUrlTemplate;
        if (string.IsNullOrWhiteSpace(template))
        {
            return token;
        }

        return template
            .Replace("{token}", Uri.EscapeDataString(token))
            .Replace("{email}", Uri.EscapeDataString(email));
    }

    private static string BuildPasswordResetEmailBody(string? displayName, string resetLinkOrToken, DateTime expiresOnUtc)
    {
        return $"""
            <p>Hi {displayName},</p>
            <p>We received a request to reset your FHIRBridge password. Use the link/token below to continue:</p>
            <p><a href="{resetLinkOrToken}">{resetLinkOrToken}</a></p>
            <p>This reset request expires at {expiresOnUtc:u}. If you did not request a password reset, you can ignore this email.</p>
            """;
    }

    private static string GenerateToken()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    // IssueRefreshToken() already returns the SHA-256 hash of the random bytes and stores
    // that same hash in the DB; the client is also given the hash directly.  Re-hashing on
    // the inbound side would produce SHA-256(SHA-256(token)) which never matches.
    private static string ComputeTokenHash(string rawToken) => rawToken;

    private static string BuildRawRefreshToken(string tokenHash) => tokenHash;

    // A valid current TOTP, or an unused backup code (which is then consumed), satisfies the second factor.
    private bool IsMfaSatisfied(User user, string? mfaCode)
    {
        if (string.IsNullOrWhiteSpace(mfaCode))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(user.MfaSecret) && _totpService.ValidateCode(user.MfaSecret, mfaCode))
        {
            return true;
        }

        return MfaBackupCodes.TryConsume(user, mfaCode, _passwordHasher);
    }

    private static void ValidatePassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 12)
        {
            throw new InvalidOperationException("Password must be at least 12 characters.");
        }

        if (!password.Any(char.IsUpper) ||
            !password.Any(char.IsLower) ||
            !password.Any(char.IsDigit) ||
            !password.Any(ch => !char.IsLetterOrDigit(ch)))
        {
            throw new InvalidOperationException("Password must include uppercase, lowercase, number, and symbol characters.");
        }
    }
}
