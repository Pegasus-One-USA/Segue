using FHIRBridge.Application.Abstractions.Audit;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Application.Services;

public sealed class UserManagementService : IUserManagementService
{
    private readonly IUserAccessRepository _repository;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ICurrentUserService _currentUserService;
    private readonly IOperationalAuditService _auditService;

    public UserManagementService(
        IUserAccessRepository repository,
        IPasswordHasher passwordHasher,
        ICurrentUserService currentUserService,
        IOperationalAuditService auditService)
    {
        _repository = repository;
        _passwordHasher = passwordHasher;
        _currentUserService = currentUserService;
        _auditService = auditService;
    }

    public async Task<IReadOnlyList<UserManagementDto>> GetUsersAsync(CancellationToken cancellationToken)
    {
        var users = await _repository.GetUsersAsync(cancellationToken);
        var dtos = new List<UserManagementDto>();

        foreach (var user in users)
        {
            dtos.Add(await ToDtoAsync(user, cancellationToken));
        }

        return dtos;
    }

    public async Task<UserManagementDto> CreateLocalUserAsync(
        CreateLocalUserRequest request,
        CancellationToken cancellationToken)
    {
        var email = NormalizeEmail(request.Email);
        ValidatePassword(request.Password);

        if (await _repository.GetUserByEmailAsync(email, cancellationToken) is not null)
        {
            throw new InvalidOperationException("A user with this email already exists.");
        }

        var user = new User(LocalExternalId(email), email, request.DisplayName);
        user.EnableLocalLogin(_passwordHasher.Hash(request.Password), request.RequirePasswordChange);

        await _repository.AddUserAsync(user, cancellationToken);
        await SetUserRolesAsync(user.Id, request.RoleNames, cancellationToken);
        await AuditAsync("LocalUserCreated", $"Local user created: {email}.", cancellationToken);

        return await ToDtoAsync(user, cancellationToken);
    }

    public async Task<UserManagementDto> UpdateLocalUserAsync(
        Guid userId,
        UpdateLocalUserRequest request,
        CancellationToken cancellationToken)
    {
        var user = await _repository.GetUserByIdAsync(userId, cancellationToken)
            ?? throw new InvalidOperationException("User was not found.");

        var email = string.IsNullOrWhiteSpace(request.Email)
            ? user.Email
            : NormalizeEmail(request.Email);

        if (!string.Equals(user.Email, email, StringComparison.OrdinalIgnoreCase) &&
            email is not null &&
            await _repository.GetUserByEmailAsync(email, cancellationToken) is not null)
        {
            throw new InvalidOperationException("A different user with this email already exists.");
        }

        user.UpdateProfile(email, request.DisplayName);
        if (email is not null && user.IsLocalLoginEnabled)
        {
            user.UpdateExternalUserId(LocalExternalId(email));
        }

        user.SetEnabled(request.IsEnabled);

        if (!string.IsNullOrWhiteSpace(request.NewPassword))
        {
            ValidatePassword(request.NewPassword);
            user.SetPassword(_passwordHasher.Hash(request.NewPassword), request.RequirePasswordChange);
        }

        await _repository.UpdateUserAsync(user, cancellationToken);
        await SetUserRolesAsync(user.Id, request.RoleNames, cancellationToken);
        await AuditAsync("LocalUserUpdated", $"Local user updated: {user.Email ?? user.ExternalUserId}.", cancellationToken);

        return await ToDtoAsync(user, cancellationToken);
    }

    private async Task SetUserRolesAsync(
        Guid userId,
        IReadOnlyCollection<string> roleNames,
        CancellationToken cancellationToken)
    {
        var roleIds = new List<Guid>();
        foreach (var roleName in roleNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var role = await _repository.GetRoleByNameAsync(roleName, cancellationToken)
                ?? throw new InvalidOperationException($"Role '{roleName}' is not configured.");

            roleIds.Add(role.Id);
        }

        await _repository.SetUserRolesAsync(userId, roleIds, cancellationToken);
    }

    private async Task<UserManagementDto> ToDtoAsync(User user, CancellationToken cancellationToken)
    {
        var roles = await _repository.GetUserRolesAsync(user.Id, cancellationToken);

        return new UserManagementDto(
            user.Id,
            user.ExternalUserId,
            user.Email,
            user.DisplayName,
            user.IsEnabled,
            user.IsLocalLoginEnabled,
            user.MustChangePassword,
            roles.Select(x => x.Name).ToArray(),
            user.CreatedOnUtc,
            user.LastLoginOnUtc);
    }

    private async Task AuditAsync(string action, string message, CancellationToken cancellationToken)
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
                "Completed",
                message,
                null,
                _currentUserService.CurrentUser.AuditName,
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

    private static string LocalExternalId(string email)
    {
        return $"local:{email}";
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
