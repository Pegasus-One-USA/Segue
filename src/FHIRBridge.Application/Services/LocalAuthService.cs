using System.Security.Cryptography;
using FHIRBridge.Application.Abstractions.Audit;
using FHIRBridge.Application.Abstractions.Notifications;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using FHIRBridge.Domain.Entities;
using Microsoft.Extensions.Options;

namespace FHIRBridge.Application.Services;

public sealed class LocalAuthService : ILocalAuthService
{
    private readonly IUserAccessRepository _repository;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IAccessTokenIssuer _accessTokenIssuer;
    private readonly ICurrentUserService _currentUserService;
    private readonly IOperationalAuditService _auditService;
    private readonly IUserActivityAuditService _activityAuditService;
    private readonly IEmailSender _emailSender;
    private readonly LocalAuthOptions _localAuthOptions;

    public LocalAuthService(
        IUserAccessRepository repository,
        IPasswordHasher passwordHasher,
        IAccessTokenIssuer accessTokenIssuer,
        ICurrentUserService currentUserService,
        IOperationalAuditService auditService,
        IUserActivityAuditService activityAuditService,
        IEmailSender emailSender,
        IOptions<LocalAuthOptions> localAuthOptions)
    {
        _repository = repository;
        _passwordHasher = passwordHasher;
        _accessTokenIssuer = accessTokenIssuer;
        _currentUserService = currentUserService;
        _auditService = auditService;
        _activityAuditService = activityAuditService;
        _emailSender = emailSender;
        _localAuthOptions = localAuthOptions.Value;
    }

    public async Task<LocalLoginResponse> LoginAsync(
        LocalLoginRequest request,
        CancellationToken cancellationToken)
    {
        var email = NormalizeEmail(request.Email);
        var user = await _repository.GetUserByEmailAsync(email, cancellationToken);

        if (user is null ||
            !user.IsEnabled ||
            !user.IsLocalLoginEnabled ||
            string.IsNullOrWhiteSpace(user.PasswordHash) ||
            !_passwordHasher.Verify(request.Password, user.PasswordHash))
        {
            await AuditAsync("LocalLogin", "Failed", $"Local login failed for {email}.", email, cancellationToken);
            await RecordActivityAsync("LoginFailed", UserActivityStatuses.Failed, email, user?.Id,
                UserActivitySeverities.Warning, "Invalid email or password.", cancellationToken);
            throw new InvalidOperationException("Invalid email or password.");
        }

        user.RecordLogin();
        await _repository.UpdateUserAsync(user, cancellationToken);
        await AuditAsync("LocalLogin", "Completed", $"Local login completed for {email}.", email, cancellationToken);
        await RecordActivityAsync("Login", UserActivityStatuses.Success, email, user.Id,
            UserActivitySeverities.Information, null, cancellationToken);

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
        await AuditAsync("PasswordChanged", "Completed", "Local password changed.", user.Email, cancellationToken);
        await RecordActivityAsync("PasswordChanged", UserActivityStatuses.Success, user.Email, user.Id,
            UserActivitySeverities.Information, null, cancellationToken);

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
            await AuditAsync("ForgotPassword", "Accepted", $"Password reset requested for {email}.", email, cancellationToken);
            return new ForgotPasswordResponse(true, null, null);
        }

        var token = GenerateToken();
        var expiresOnUtc = DateTime.UtcNow.AddMinutes(30);
        user.SetPasswordResetToken(_passwordHasher.Hash(token), expiresOnUtc);
        await _repository.UpdateUserAsync(user, cancellationToken);
        await AuditAsync("ForgotPassword", "Accepted", $"Password reset token generated for {email}.", email, cancellationToken);

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
        await AuditAsync("PasswordReset", "Completed", $"Password reset completed for {email}.", email, cancellationToken);
        await RecordActivityAsync("PasswordReset", UserActivityStatuses.Success, email, user.Id,
            UserActivitySeverities.Warning, null, cancellationToken);
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

        await AuditAsync("TokenRefreshed", "Completed", $"Token refreshed for {user.Email}.", user.Email, cancellationToken);

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
        await AuditAsync("SsoLogin", "Completed", $"SSO login completed for {user.Email}.", user.Email, cancellationToken);
        await RecordActivityAsync("Login", UserActivityStatuses.Success, user.Email, user.Id,
            UserActivitySeverities.Information, null, cancellationToken);

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
        await AuditAsync("Logout", "Completed", $"User logged out: {user.Email}.", user.Email, cancellationToken);
    }

    private Task RecordActivityAsync(
        string activity,
        string status,
        string? email,
        Guid? userId,
        string severity,
        string? failureReason,
        CancellationToken cancellationToken)
    {
        return _activityAuditService.RecordAsync(
            new RecordUserActivityRequest(
                UserId: userId,
                UserEmail: email ?? _currentUserService.CurrentUser.AuditName,
                Category: UserActivityCategories.Authentication,
                Activity: activity,
                Status: status,
                EntityName: nameof(User),
                EntityId: userId,
                FailureReason: failureReason,
                Severity: severity),
            cancellationToken);
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
            token.AccessToken,
            token.TokenType,
            token.ExpiresOnUtc,
            user.MustChangePassword,
            new UserProfileDto(
                user.Id,
                user.ExternalUserId,
                user.Email,
                user.DisplayName,
                roleNames),
            BuildRawRefreshToken(refreshHash),
            refreshExpiry);
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

    private async Task AuditAsync(
        string action,
        string status,
        string message,
        string? triggeredBy,
        CancellationToken cancellationToken)
    {
        await _auditService.RecordAsync(
            new RecordOperationalAuditLogRequest(
                null,
                null,
                null,
                null,
                null,
                null,
                action,
                status,
                message,
                null,
                triggeredBy ?? _currentUserService.CurrentUser.AuditName,
                null),
            cancellationToken);
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
