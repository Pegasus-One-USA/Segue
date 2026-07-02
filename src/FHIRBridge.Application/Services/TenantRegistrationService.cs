using FHIRBridge.Application.Abstractions.Audit;
using FHIRBridge.Application.Abstractions.Notifications;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using FHIRBridge.Domain.Aggregates;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Application.Services;

/// <summary>
/// Handles the tenant self-registration flow. Creates a new Tenant, seeds the four default tenant
/// roles, creates the Super Admin user, and returns a JWT so the caller lands directly on the dashboard.
/// </summary>
public sealed class TenantRegistrationService : ITenantRegistrationService
{
    private readonly ITenantConfigurationRepository _tenantRepository;
    private readonly IUserAccessRepository _userRepository;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IAccessTokenIssuer _accessTokenIssuer;
    private readonly IOperationalAuditService _auditService;
    private readonly IEmailSender _emailSender;

    public TenantRegistrationService(
        ITenantConfigurationRepository tenantRepository,
        IUserAccessRepository userRepository,
        IPasswordHasher passwordHasher,
        IAccessTokenIssuer accessTokenIssuer,
        IOperationalAuditService auditService,
        IEmailSender emailSender)
    {
        _tenantRepository = tenantRepository;
        _userRepository = userRepository;
        _passwordHasher = passwordHasher;
        _accessTokenIssuer = accessTokenIssuer;
        _auditService = auditService;
        _emailSender = emailSender;
    }

    public async Task<RegisterTenantResponse> RegisterAsync(
        RegisterTenantRequest request,
        CancellationToken cancellationToken)
    {
        var email = NormalizeEmail(request.AdminEmail);
        ValidatePassword(request.AdminPassword);

        var existingUser = await _userRepository.GetUserByEmailAsync(email, cancellationToken);
        if (existingUser is not null)
        {
            throw new InvalidOperationException("A user with this email address already exists.");
        }

        // 1. Create tenant.
        var tenant = new Tenant(request.OrgName, GenerateTenantCode(request.OrgName));
        await _tenantRepository.AddAsync(tenant, cancellationToken);

        // 2. Seed the four default tenant roles.
        var (superAdminRole, adminRole, operationsRole, auditRole) =
            await SeedDefaultRolesAsync(tenant.Id, cancellationToken);

        // 3. Create the Super Admin user.
        var user = new User($"local:{email}", email, BuildDisplayName(request.AdminFirstName, request.AdminLastName, email));
        user.UpdateName(request.AdminFirstName, request.AdminLastName);
        user.SetHomeTenant(tenant.Id);
        user.EnableLocalLogin(_passwordHasher.Hash(request.AdminPassword), mustChangePassword: false);

        await _userRepository.AddUserAsync(user, cancellationToken);
        await _userRepository.AddUserRoleAsync(user.Id, superAdminRole.Id, cancellationToken);

        var tenantUser = new TenantUser(tenant.Id, user.Id, superAdminRole.Id);
        await _userRepository.AddTenantUserAsync(tenantUser, cancellationToken);

        // 4. Issue tokens.
        var permissionCodes = await GetRolePermissionCodesAsync(superAdminRole.Id, cancellationToken);
        var accessToken = _accessTokenIssuer.Issue(user, [superAdminRole.Name], permissionCodes);
        var (refreshHash, refreshExpiry) = _accessTokenIssuer.IssueRefreshToken();
        user.SetRefreshToken(refreshHash, refreshExpiry);
        await _userRepository.UpdateUserAsync(user, cancellationToken);

        await _auditService.RecordAsync(
            new RecordOperationalAuditLogRequest(
                tenant.Id,
                null, null, null, null, null, null,
                "TenantRegistered",
                "Completed",
                $"Tenant '{request.OrgName}' registered. Super Admin: {email}.",
                null,
                email,
                null),
            cancellationToken);

        await _emailSender.SendAsync(
            email,
            "Welcome to FHIRBridge",
            BuildWelcomeEmailBody(request.OrgName, user.DisplayName),
            cancellationToken);

        return new RegisterTenantResponse(
            tenant.Id,
            user.Id,
            accessToken.AccessToken,
            accessToken.TokenType,
            accessToken.ExpiresOnUtc,
            refreshHash,
            refreshExpiry);
    }

    private async Task<(Role SuperAdmin, Role Admin, Role Operations, Role Audit)>
        SeedDefaultRolesAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var superAdmin = new Role(Guid.NewGuid(), UnifiedRoles.SuperAdmin,
            "Full access to user management, role management, workflows, settings, and billing.",
            isSystem: false, tenantId: tenantId, isDefault: true);

        var admin = new Role(Guid.NewGuid(), UnifiedRoles.Admin,
            "Administers configuration and users within the tenant.",
            isSystem: false, tenantId: tenantId, isDefault: true);

        var operations = new Role(Guid.NewGuid(), UnifiedRoles.Operations,
            "Builds and runs pipeline configurations, and reviews data and audit output.",
            isSystem: false, tenantId: tenantId, isDefault: true);

        var audit = new Role(Guid.NewGuid(), UnifiedRoles.Audit,
            "Read-only access to configuration and audit logs.",
            isSystem: false, tenantId: tenantId, isDefault: true);

        await _userRepository.AddRoleAsync(superAdmin, cancellationToken);
        await _userRepository.AddRoleAsync(admin, cancellationToken);
        await _userRepository.AddRoleAsync(operations, cancellationToken);
        await _userRepository.AddRoleAsync(audit, cancellationToken);

        // Permission grants come from the single source of truth shared with the platform's
        // own SuperAdmin/Admin/Operations/Audit roles, so SuperAdmin always gets every
        // permission and new permissions never need a second hardcoded list here.
        await _userRepository.SetRolePermissionsAsync(
            superAdmin.Id, UnifiedRolePermissionSeed.Grants[SeededSecurityIds.SuperAdminRoleId].ToArray(), cancellationToken);
        await _userRepository.SetRolePermissionsAsync(
            admin.Id, UnifiedRolePermissionSeed.Grants[SeededSecurityIds.AdminRoleId].ToArray(), cancellationToken);
        await _userRepository.SetRolePermissionsAsync(
            operations.Id, UnifiedRolePermissionSeed.Grants[SeededSecurityIds.OperationsRoleId].ToArray(), cancellationToken);
        await _userRepository.SetRolePermissionsAsync(
            audit.Id, UnifiedRolePermissionSeed.Grants[SeededSecurityIds.AuditRoleId].ToArray(), cancellationToken);

        return (superAdmin, admin, operations, audit);
    }

    private async Task<string[]> GetRolePermissionCodesAsync(Guid roleId, CancellationToken cancellationToken)
    {
        var permissions = await _userRepository.GetRolePermissionsAsync(roleId, cancellationToken);

        return permissions.Select(p => p.Name).ToArray();
    }

    private static string NormalizeEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            throw new InvalidOperationException("Admin email is required.");
        }

        return email.Trim().ToLowerInvariant();
    }

    private static string GenerateTenantCode(string orgName)
    {
        var clean = new string(orgName
            .Where(char.IsLetterOrDigit)
            .Take(8)
            .ToArray())
            .ToUpperInvariant();

        return string.IsNullOrWhiteSpace(clean)
            ? Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()
            : clean;
    }

    private static string BuildWelcomeEmailBody(string orgName, string? displayName)
    {
        return $"""
            <p>Hi {displayName},</p>
            <p>Your FHIRBridge workspace for <strong>{orgName}</strong> is ready. You're signed in as the Super Admin
            and can start inviting teammates, configuring pipelines, and managing roles right away.</p>
            """;
    }

    private static string BuildDisplayName(string? firstName, string? lastName, string email)
    {
        var parts = new[] { firstName, lastName }
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToArray();

        return parts.Length > 0 ? string.Join(" ", parts) : email;
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
