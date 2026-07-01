using System.Security.Cryptography;
using FHIRBridge.Application.Abstractions.Audit;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Application.Services;

public sealed class LocalAuthService : ILocalAuthService
{
    private readonly IUserAccessRepository _repository;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IAccessTokenIssuer _accessTokenIssuer;
    private readonly ICurrentUserService _currentUserService;
    private readonly IOperationalAuditService _auditService;
    private readonly IUserActivityAuditService _activityAuditService;

    public LocalAuthService(
        IUserAccessRepository repository,
        IPasswordHasher passwordHasher,
        IAccessTokenIssuer accessTokenIssuer,
        ICurrentUserService currentUserService,
        IOperationalAuditService auditService,
        IUserActivityAuditService activityAuditService)
    {
        _repository = repository;
        _passwordHasher = passwordHasher;
        _accessTokenIssuer = accessTokenIssuer;
        _currentUserService = currentUserService;
        _auditService = auditService;
        _activityAuditService = activityAuditService;
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

        var token = GenerateResetToken();
        var expiresOnUtc = DateTime.UtcNow.AddMinutes(30);
        user.SetPasswordResetToken(_passwordHasher.Hash(token), expiresOnUtc);
        await _repository.UpdateUserAsync(user, cancellationToken);
        await AuditAsync("ForgotPassword", "Accepted", $"Password reset token generated for {email}.", email, cancellationToken);

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
                TenantId: null,
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
        var token = _accessTokenIssuer.Issue(user, roleNames);
        var memberships = await ToTenantMembershipsAsync(user.Id, cancellationToken);

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
                roleNames,
                memberships));
    }

    private async Task<IReadOnlyList<TenantUserDto>> ToTenantMembershipsAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        var memberships = await _repository.GetTenantMembershipsByUserIdAsync(userId, cancellationToken);
        var dtos = new List<TenantUserDto>();
        foreach (var membership in memberships)
        {
            var role = await GetRoleNameByIdAsync(membership.RoleId, cancellationToken);
            var user = await _repository.GetUserByIdAsync(membership.UserId, cancellationToken)
                ?? throw new InvalidOperationException("Tenant user references a missing user.");

            dtos.Add(new TenantUserDto(
                membership.Id,
                membership.TenantId,
                membership.UserId,
                user.ExternalUserId,
                user.Email,
                user.DisplayName,
                role,
                membership.IsEnabled));
        }

        return dtos;
    }

    private async Task<string> GetRoleNameByIdAsync(Guid roleId, CancellationToken cancellationToken)
    {
        var role = await _repository.GetRoleByIdAsync(roleId, cancellationToken);

        return role?.Name ?? "Unknown";
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
                Guid.Empty,
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

    private static string GenerateResetToken()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
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
