using System.Security.Cryptography;
using FHIRBridge.Application.Abstractions.Notifications;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using Microsoft.Extensions.Options;

namespace FHIRBridge.Application.Services;

public sealed class UserManagementService : IUserManagementService
{
    private const int InvitationTokenLifetimeHours = 48;

    private readonly IUserAccessRepository _repository;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ICurrentUserService _currentUserService;
    private readonly IUserDisplayNameResolver _userDisplayNameResolver;
    private readonly IEmailSender _emailSender;
    private readonly IExternalTokenValidator _externalTokenValidator;
    private readonly ILocalAuthService _localAuthService;
    private readonly LocalAuthOptions _localAuthOptions;

    public UserManagementService(
        IUserAccessRepository repository,
        IPasswordHasher passwordHasher,
        ICurrentUserService currentUserService,
        IUserDisplayNameResolver userDisplayNameResolver,
        IEmailSender emailSender,
        IExternalTokenValidator externalTokenValidator,
        ILocalAuthService localAuthService,
        IOptions<LocalAuthOptions> localAuthOptions)
    {
        _repository = repository;
        _passwordHasher = passwordHasher;
        _currentUserService = currentUserService;
        _userDisplayNameResolver = userDisplayNameResolver;
        _emailSender = emailSender;
        _externalTokenValidator = externalTokenValidator;
        _localAuthService = localAuthService;
        _localAuthOptions = localAuthOptions.Value;
    }

    public async Task<IReadOnlyList<UserManagementDto>> GetUsersAsync(CancellationToken cancellationToken)
    {
        var users = await _repository.GetUsersAsync(cancellationToken);
        var dtos = new List<UserManagementDto>();

        foreach (var user in users)
        {
            dtos.Add(await ToManagementDtoAsync(user, cancellationToken));
        }

        return dtos;
    }

    public async Task<UserDetailDto> GetUserByIdAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await _repository.GetUserByIdAsync(userId, cancellationToken)
            ?? throw new InvalidOperationException("User was not found.");

        return await ToDetailDtoAsync(user, invitationToken: null, cancellationToken);
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

        var user = new User(LocalExternalId(email), email, request.DisplayName, request.TenantId);
        user.UpdateName(request.FirstName, request.LastName);
        user.EnableLocalLogin(_passwordHasher.Hash(request.Password), request.RequirePasswordChange);
        user.SetMustSetupMfa(request.RequireMfa);

        await _repository.AddUserAsync(user, cancellationToken);
        await SetUserRolesAsync(user.Id, request.RoleNames, cancellationToken);

        return await ToManagementDtoAsync(user, cancellationToken);
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

        return await ToManagementDtoAsync(user, cancellationToken);
    }

    public async Task<UserDetailDto> InviteUserAsync(
        InviteUserRequest request,
        CancellationToken cancellationToken)
    {
        var email = NormalizeEmail(request.Email);

        var role = await _repository.GetRoleByIdAsync(request.RoleId, cancellationToken)
            ?? throw new InvalidOperationException("The specified role does not exist.");

        var existingUser = await _repository.GetUserByEmailAsync(email, cancellationToken);
        if (existingUser is not null)
        {
            throw new InvalidOperationException("A user with this email already exists.");
        }

        var rawToken = GenerateToken();
        var tokenHash = _passwordHasher.Hash(rawToken);
        var expiresOnUtc = DateTime.UtcNow.AddHours(InvitationTokenLifetimeHours);

        var user = new User(LocalExternalId(email), email, null, request.TenantId);
        user.UpdateName(request.FirstName, request.LastName);
        user.SetInvited(tokenHash, expiresOnUtc);
        // HIPAA hardening: MFA is compulsory for every invited user — hardcoded true, not an admin choice.
        user.SetMustSetupMfa(true);

        await _repository.AddUserAsync(user, cancellationToken);
        await _repository.AddUserRoleAsync(user.Id, role.Id, cancellationToken);

        await _emailSender.SendAsync(
            email,
            "You've been invited to Segue",
            BuildInviteEmailBody(request.FirstName, role.Name, BuildInviteLink(email, rawToken), expiresOnUtc),
            cancellationToken);

        return await ToDetailDtoAsync(user, invitationToken: rawToken, cancellationToken);
    }

    public async Task<UserDetailDto> ResendInviteAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await _repository.GetUserByIdAsync(userId, cancellationToken)
            ?? throw new InvalidOperationException("User was not found.");

        if (user.Status != UserStatus.Invited)
        {
            throw new InvalidOperationException("Only a pending invitation can be resent.");
        }

        var rawToken = GenerateToken();
        var tokenHash = _passwordHasher.Hash(rawToken);
        var expiresOnUtc = DateTime.UtcNow.AddHours(InvitationTokenLifetimeHours);

        // Reissuing the invitation refreshes the single-use token and its expiry; any previously sent token is invalidated.
        user.SetInvited(tokenHash, expiresOnUtc);
        await _repository.UpdateUserAsync(user, cancellationToken);

        var roles = await _repository.GetUserRolesAsync(user.Id, cancellationToken);
        var roleName = roles.FirstOrDefault()?.Name ?? "user";

        await _emailSender.SendAsync(
            user.Email!,
            "Your Segue invitation (resent)",
            BuildInviteEmailBody(user.FirstName, roleName, BuildInviteLink(user.Email!, rawToken), expiresOnUtc),
            cancellationToken);

        return await ToDetailDtoAsync(user, invitationToken: rawToken, cancellationToken);
    }

    public async Task<UserDetailDto> AcceptInviteAsync(
        AcceptInviteRequest request,
        CancellationToken cancellationToken)
    {
        ValidatePassword(request.Password);

        var email = NormalizeEmail(request.Email);
        var user = await _repository.GetUserByEmailAsync(email, cancellationToken)
            ?? throw new InvalidOperationException("Invitation is invalid or expired.");

        if (user.Status != UserStatus.Invited ||
            string.IsNullOrWhiteSpace(user.InvitationTokenHash) ||
            user.InvitationTokenExpiresOnUtc is null ||
            user.InvitationTokenExpiresOnUtc < DateTime.UtcNow ||
            !_passwordHasher.Verify(request.InvitationToken, user.InvitationTokenHash))
        {
            throw new InvalidOperationException("Invitation is invalid or expired.");
        }

        if (!request.AcceptTerms)
        {
            throw new InvalidOperationException("You must accept the Terms and Conditions to continue.");
        }

        user.AcceptInvitation(
            _passwordHasher.Hash(request.Password),
            request.FirstName,
            request.LastName);

        await _repository.UpdateUserAsync(user, cancellationToken);

        return await ToDetailDtoAsync(user, invitationToken: null, cancellationToken);
    }

    public async Task<LocalLoginResponse> AcceptInviteViaSsoAsync(
        AcceptInviteSsoRequest request,
        CancellationToken cancellationToken)
    {
        var email = NormalizeEmail(request.Email);
        var user = await _repository.GetUserByEmailAsync(email, cancellationToken)
            ?? throw new InvalidOperationException("Invitation is invalid or expired.");

        // Same invitation-token guards as AcceptInviteAsync.
        if (user.Status != UserStatus.Invited ||
            string.IsNullOrWhiteSpace(user.InvitationTokenHash) ||
            user.InvitationTokenExpiresOnUtc is null ||
            user.InvitationTokenExpiresOnUtc < DateTime.UtcNow ||
            !_passwordHasher.Verify(request.InvitationToken, user.InvitationTokenHash))
        {
            throw new InvalidOperationException("Invitation is invalid or expired.");
        }

        if (!request.AcceptTerms)
        {
            throw new InvalidOperationException("You must accept the Terms and Conditions to continue.");
        }

        var identity = await _externalTokenValidator.ValidateAsync(request.Provider, request.Token, cancellationToken);

        // The external identity must match the invited address so an invite cannot be redeemed by a
        // different Google/Entra account. ArgumentException maps to HTTP 400.
        if (string.IsNullOrWhiteSpace(identity.Email) ||
            !string.Equals(identity.Email.Trim(), email, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The external identity email does not match the invited email address.");
        }

        // Names captured at invite time are preserved; pass null so AcceptInvitationViaSso keeps them.
        user.AcceptInvitationViaSso(
            identity.Subject,
            request.Provider,
            firstName: null,
            lastName: null);

        await _repository.UpdateUserAsync(user, cancellationToken);

        return await _localAuthService.IssueSessionAsync(user, cancellationToken);
    }

    public async Task<UserDetailDto> UpdateUserStatusAsync(
        Guid userId,
        UpdateUserStatusRequest request,
        CancellationToken cancellationToken)
    {
        var user = await _repository.GetUserByIdAsync(userId, cancellationToken)
            ?? throw new InvalidOperationException("User was not found.");

        if (!request.IsEnabled)
        {
            var currentUserId = _currentUserService.CurrentUser.ExternalUserId;
            if (!string.IsNullOrWhiteSpace(currentUserId) &&
                string.Equals(user.ExternalUserId, currentUserId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("You cannot deactivate your own account.");
            }
        }

        var wasEnabled = user.IsEnabled;
        user.SetEnabled(request.IsEnabled);
        await _repository.UpdateUserAsync(user, cancellationToken);

        return await ToDetailDtoAsync(user, invitationToken: null, cancellationToken);
    }

    public async Task<UserDetailDto> DisableMfaForUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await _repository.GetUserByIdAsync(userId, cancellationToken)
            ?? throw new InvalidOperationException("User was not found.");

        if (user.MfaEnabled)
        {
            user.DisableMfa();
            await _repository.UpdateUserAsync(user, cancellationToken);
        }

        return await ToDetailDtoAsync(user, invitationToken: null, cancellationToken);
    }

    /// <summary>
    /// Admin policy toggle: requires (or stops requiring) this account to have MFA enabled. Setting
    /// this while the user has no MFA enrolled gates their next login to enrollment only — see
    /// LocalAuthService's RequiresMfaSetup handling and the corresponding gate middleware in Program.cs.
    /// </summary>
    public async Task<UserDetailDto> SetMfaRequirementAsync(Guid userId, bool required, CancellationToken cancellationToken)
    {
        var user = await _repository.GetUserByIdAsync(userId, cancellationToken)
            ?? throw new InvalidOperationException("User was not found.");

        var wasRequired = user.MustSetupMfa;
        user.SetMustSetupMfa(required);
        await _repository.UpdateUserAsync(user, cancellationToken);

        return await ToDetailDtoAsync(user, invitationToken: null, cancellationToken);
    }

    public async Task DeleteUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await _repository.GetUserByIdAsync(userId, cancellationToken)
            ?? throw new InvalidOperationException("User was not found.");

        var currentUserId = _currentUserService.CurrentUser.ExternalUserId;
        if (!string.IsNullOrWhiteSpace(currentUserId) &&
            string.Equals(user.ExternalUserId, currentUserId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("You cannot delete your own account.");
        }

        await _repository.DeleteUserAsync(user, cancellationToken);
    }

    public async Task<IReadOnlyList<RoleDto>> GetUserRolesAsync(Guid userId, CancellationToken cancellationToken)
    {
        var roles = await _repository.GetUserRolesAsync(userId, cancellationToken);
        var dtos = new List<RoleDto>();

        foreach (var role in roles)
        {
            var permissions = await _repository.GetRolePermissionsAsync(role.Id, cancellationToken);
            dtos.Add(new RoleDto(
                role.Id,
                role.Name,
                role.Description,
                permissions.Select(p => new PermissionDto(p.Id, p.Name, p.DisplayName, p.Description, p.GroupId, p.IsVisible)).ToArray(),
                role.IsSystem));
        }

        return dtos;
    }

    public async Task<UserDetailDto> AssignUserRoleAsync(
        Guid userId,
        AssignUserRoleRequest request,
        CancellationToken cancellationToken)
    {
        var user = await _repository.GetUserByIdAsync(userId, cancellationToken)
            ?? throw new InvalidOperationException("User was not found.");

        var role = await _repository.GetRoleByIdAsync(request.RoleId, cancellationToken)
            ?? throw new InvalidOperationException("Role was not found.");

        var currentRoles = await _repository.GetUserRolesAsync(userId, cancellationToken);
        if (currentRoles.Any(r => r.Id == request.RoleId))
        {
            throw new InvalidOperationException("This role is already assigned to the user.");
        }

        await _repository.AddUserRoleAsync(userId, role.Id, cancellationToken);

        return await ToDetailDtoAsync(user, invitationToken: null, cancellationToken);
    }

    public async Task RemoveUserRoleAsync(Guid userId, Guid roleId, CancellationToken cancellationToken)
    {
        var user = await _repository.GetUserByIdAsync(userId, cancellationToken)
            ?? throw new InvalidOperationException("User was not found.");

        var role = await _repository.GetRoleByIdAsync(roleId, cancellationToken)
            ?? throw new InvalidOperationException("Role was not found.");

        var currentUserId = _currentUserService.CurrentUser.ExternalUserId;
        if (role.Name == "SuperAdmin" &&
            !string.IsNullOrWhiteSpace(currentUserId) &&
            string.Equals(user.ExternalUserId, currentUserId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("You cannot remove your own Super Admin role.");
        }

        await _repository.RemoveUserRoleAsync(userId, roleId, cancellationToken);
    }

    public async Task<IReadOnlyList<PermissionAllocationDto>> GetUserPermissionAllocationsAsync(
        Guid userId, CancellationToken cancellationToken)
    {
        var allocations = await _repository.GetUserPermissionAllocationsAsync(userId, cancellationToken);

        return allocations
            .Select(a => new PermissionAllocationDto(a.Permission.Id, a.Permission.Name, a.Permission.Description, a.IsEnabled))
            .ToArray();
    }

    public async Task<UserDetailDto> SetUserPermissionAllocationAsync(
        Guid userId,
        Guid permissionId,
        UpsertUserPermissionAllocationRequest request,
        CancellationToken cancellationToken)
    {
        var user = await _repository.GetUserByIdAsync(userId, cancellationToken)
            ?? throw new InvalidOperationException("User was not found.");

        var permission = await _repository.GetPermissionByIdAsync(permissionId, cancellationToken)
            ?? throw new InvalidOperationException("Permission was not found.");

        await _repository.AddUserPermissionAllocationAsync(userId, permissionId, request.IsEnabled, cancellationToken);
        await ForceReauthenticationAsync(user, cancellationToken);

        return await ToDetailDtoAsync(user, invitationToken: null, cancellationToken);
    }

    public async Task RemoveUserPermissionAllocationAsync(Guid userId, Guid permissionId, CancellationToken cancellationToken)
    {
        var user = await _repository.GetUserByIdAsync(userId, cancellationToken)
            ?? throw new InvalidOperationException("User was not found.");

        var permission = await _repository.GetPermissionByIdAsync(permissionId, cancellationToken)
            ?? throw new InvalidOperationException("Permission was not found.");

        await _repository.RemoveUserPermissionAllocationAsync(userId, permissionId, cancellationToken);
        await ForceReauthenticationAsync(user, cancellationToken);
    }

    public async Task<UserDetailDto> SetUserPermissionAllocationsAsync(
        Guid userId,
        SetUserPermissionAllocationsRequest request,
        CancellationToken cancellationToken)
    {
        var user = await _repository.GetUserByIdAsync(userId, cancellationToken)
            ?? throw new InvalidOperationException("User was not found.");

        await _repository.SetUserPermissionAllocationsAsync(userId, request.PermissionIdToIsEnabled, cancellationToken);
        await ForceReauthenticationAsync(user, cancellationToken);

        return await ToDetailDtoAsync(user, invitationToken: null, cancellationToken);
    }

    /// <summary>
    /// Clears the user's refresh token so their next silent-refresh attempt fails and they're forced
    /// through a real login, picking up the newly resolved permission set. The access token already in
    /// their possession remains valid for its existing (short) lifetime — an accepted JWT tradeoff.
    /// </summary>
    private async Task ForceReauthenticationAsync(User user, CancellationToken cancellationToken)
    {
        user.ClearRefreshToken();
        await _repository.UpdateUserAsync(user, cancellationToken);
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
                ?? throw new InvalidOperationException("Something went wrong assigning roles. Please contact support.");

            roleIds.Add(role.Id);
        }

        await _repository.SetUserRolesAsync(userId, roleIds, cancellationToken);
    }

    private async Task<UserDetailDto> ToDetailDtoAsync(
        User user,
        string? invitationToken,
        CancellationToken cancellationToken)
    {
        var roles = await _repository.GetUserRolesAsync(user.Id, cancellationToken);
        var roleDtos = new List<RoleDto>();
        foreach (var role in roles)
        {
            var permissions = await _repository.GetRolePermissionsAsync(role.Id, cancellationToken);
            roleDtos.Add(new RoleDto(
                role.Id,
                role.Name,
                role.Description,
                permissions.Select(p => new PermissionDto(p.Id, p.Name, p.DisplayName, p.Description, p.GroupId, p.IsVisible)).ToArray(),
                role.IsSystem));
        }

        var allocations = await _repository.GetUserPermissionAllocationsAsync(user.Id, cancellationToken);
        var allocationDtos = allocations
            .Select(a => new PermissionAllocationDto(a.Permission.Id, a.Permission.Name, a.Permission.Description, a.IsEnabled))
            .ToArray();

        return new UserDetailDto(
            user.Id,
            user.Email,
            user.FirstName,
            user.LastName,
            user.DisplayName,
            user.Status,
            user.IsEnabled,
            roleDtos,
            allocationDtos,
            user.CreatedOnUtc,
            user.LastLoginOnUtc,
            invitationToken,
            user.MfaEnabled,
            user.MustSetupMfa);
    }

    private async Task<UserManagementDto> ToManagementDtoAsync(User user, CancellationToken cancellationToken)
    {
        var roles = await _repository.GetUserRolesAsync(user.Id, cancellationToken);
        var createdBy = await _userDisplayNameResolver.ResolveOneAsync(user.CreatedBy, cancellationToken);
        var modifiedBy = await _userDisplayNameResolver.ResolveOneAsync(user.ModifiedBy, cancellationToken);

        return new UserManagementDto(
            user.Id,
            user.ExternalUserId,
            user.Email,
            user.EffectiveDisplayName,
            user.Status,
            user.IsEnabled,
            user.IsLocalLoginEnabled,
            user.MustChangePassword,
            roles.Select(x => x.Name).ToArray(),
            user.CreatedOnUtc,
            user.LastLoginOnUtc,
            user.MfaEnabled,
            user.MustSetupMfa,
            createdBy,
            user.ModifiedOnUtc,
            modifiedBy);
    }

    private static string NormalizeEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            throw new InvalidOperationException("Email is required.");
        }

        return email.Trim().ToLowerInvariant();
    }

    private static string LocalExternalId(string email) => $"local:{email}";

    private string BuildInviteLink(string email, string token)
    {
        var template = _localAuthOptions.AcceptInviteUrlTemplate;
        if (string.IsNullOrWhiteSpace(template))
        {
            return token;
        }

        return template
            .Replace("{token}", Uri.EscapeDataString(token))
            .Replace("{email}", Uri.EscapeDataString(email));
    }

    private static string BuildInviteEmailBody(string? firstName, string roleName, string inviteLinkOrToken, DateTime expiresOnUtc)
    {
        var greetingName = string.IsNullOrWhiteSpace(firstName) ? "there" : firstName;

        return $"""
            <p>Hi {greetingName},</p>
            <p>You've been invited to join Segue as a <strong>{roleName}</strong>. Use the link/token below to
            accept your invitation and set your password:</p>
            <p><a href="{inviteLinkOrToken}">{inviteLinkOrToken}</a></p>
            <p>This invitation expires at {expiresOnUtc:u}.</p>
            """;
    }

    private static string GenerateToken()
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
