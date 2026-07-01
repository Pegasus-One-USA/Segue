using FHIRBridge.Application.Abstractions.Audit;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
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

    public TenantRegistrationService(
        ITenantConfigurationRepository tenantRepository,
        IUserAccessRepository userRepository,
        IPasswordHasher passwordHasher,
        IAccessTokenIssuer accessTokenIssuer,
        IOperationalAuditService auditService)
    {
        _tenantRepository = tenantRepository;
        _userRepository = userRepository;
        _passwordHasher = passwordHasher;
        _accessTokenIssuer = accessTokenIssuer;
        _auditService = auditService;
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
        var (superAdminRole, workflowDesignerRole, operatorRole, viewerRole) =
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

        return new RegisterTenantResponse(
            tenant.Id,
            user.Id,
            accessToken.AccessToken,
            accessToken.TokenType,
            accessToken.ExpiresOnUtc,
            refreshHash,
            refreshExpiry);
    }

    private async Task<(Role SuperAdmin, Role WorkflowDesigner, Role Operator, Role Viewer)>
        SeedDefaultRolesAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var allPermissions = await _userRepository.GetPermissionsAsync(cancellationToken);
        var permById = allPermissions.ToDictionary(p => p.Name, p => p.Id);

        var superAdmin = new Role(Guid.NewGuid(), "SuperAdmin",
            "Full access to user management, role management, workflows, settings, and billing.",
            isSystem: false, tenantId: tenantId, isDefault: true);

        var workflowDesigner = new Role(Guid.NewGuid(), "WorkflowDesigner",
            "Creates and edits workflows.",
            isSystem: false, tenantId: tenantId, isDefault: true);

        var @operator = new Role(Guid.NewGuid(), "Operator",
            "Runs workflows and views results.",
            isSystem: false, tenantId: tenantId, isDefault: true);

        var viewer = new Role(Guid.NewGuid(), "Viewer",
            "Read-only access — cannot edit anything.",
            isSystem: false, tenantId: tenantId, isDefault: true);

        await _userRepository.AddRoleAsync(superAdmin, cancellationToken);
        await _userRepository.AddRoleAsync(workflowDesigner, cancellationToken);
        await _userRepository.AddRoleAsync(@operator, cancellationToken);
        await _userRepository.AddRoleAsync(viewer, cancellationToken);

        // Super Admin: all user-module permissions.
        await AssignPermissionsAsync(superAdmin.Id, permById, [
            "user.invite", "user.view", "user.edit", "user.deactivate",
            "role.create", "role.edit", "role.delete", "role.assign", "role.view",
            "workflow.create", "workflow.edit", "workflow.delete", "workflow.run", "workflow.view",
            "tenant.settings.edit", "tenant.billing.view",
            "report.view", "payload.view"
        ], cancellationToken);

        // Workflow Designer.
        await AssignPermissionsAsync(workflowDesigner.Id, permById, [
            "workflow.create", "workflow.edit", "workflow.delete", "workflow.run", "payload.view"
        ], cancellationToken);

        // Operator.
        await AssignPermissionsAsync(@operator.Id, permById, [
            "workflow.run", "workflow.view", "payload.view"
        ], cancellationToken);

        // Viewer.
        await AssignPermissionsAsync(viewer.Id, permById, [
            "workflow.view", "report.view"
        ], cancellationToken);

        return (superAdmin, workflowDesigner, @operator, viewer);
    }

    private async Task AssignPermissionsAsync(
        Guid roleId,
        Dictionary<string, Guid> permById,
        string[] codes,
        CancellationToken cancellationToken)
    {
        var ids = codes
            .Where(c => permById.ContainsKey(c))
            .Select(c => permById[c])
            .ToArray();

        await _userRepository.SetRolePermissionsAsync(roleId, ids, cancellationToken);
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
