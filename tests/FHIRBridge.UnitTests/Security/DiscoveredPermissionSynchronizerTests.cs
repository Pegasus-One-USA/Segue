using FHIRBridge.Api.Rbac;
using FHIRBridge.Api.Security;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Security;
using FHIRBridge.Domain.Entities;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace FHIRBridge.UnitTests.Security;

/// <summary>
/// RBAC redesign Step 5: <see cref="DiscoveredPermissionSynchronizer"/> (extracted, unchanged in logic,
/// from Program.cs's former SyncDiscoveredPermissionsAsync local function — see its own doc comment) used
/// to grant a brand-new discovered permission to EVERY existing role unconditionally. That's the bug this
/// step fixes: a role with <see cref="Role.IsFullAccess"/> already automatically holds every active
/// permission via <c>CachedUserPermissionsProvider</c> (Step 2) and needs no explicit
/// <c>PermissionAllocation</c> row; a normal custom role must never silently receive a permission nobody
/// explicitly assigned it. Only the two remaining non-full-access system roles (Operations, Audit) still
/// get an explicit row, preserving their historical "never lose access" guarantee unchanged.
///
/// <see cref="DiscoveredPermissionSynchronizerTestController"/> below is a throwaway, [StandardPermission]-
/// decorated controller that exists ONLY so PermissionCatalog.DiscoveredPermissions has something
/// deterministic and fully test-controlled to discover — pointed at via SyncAsync's scanAssembly
/// parameter, which defaults to FHIRBridge.Api (production behavior) and is never used any other way.
/// </summary>
public sealed class DiscoveredPermissionSynchronizerTests
{
    // An arbitrary but real, valid (group, action) pair unrelated to any permission this test's mocked
    // repository is told already exists -- what makes it "new" is purely the mock's GetPermissionsAsync
    // configuration below, not anything about the pair itself.
    private static readonly Guid NewPermissionId = PermissionTaxonomy.BuildPermissionId(PermissionGroupCode.Sample, PermissionActionCode.Assign);
    private static readonly string NewPermissionCode = PermissionTaxonomy.BuildPermissionCode(PermissionGroupCode.Sample, PermissionActionCode.Assign);

    private static Role SystemRole(Guid id, string name, bool isFullAccess) =>
        Build(new Role(id, name, $"{name} role.", isSystem: true), isFullAccess);

    private static Role CustomRole(string name, bool isFullAccess) =>
        Build(new Role(Guid.NewGuid(), name, $"{name} role.", isSystem: false), isFullAccess);

    private static Role Build(Role role, bool isFullAccess)
    {
        if (isFullAccess)
        {
            role.SetFullAccess(true);
        }

        return role;
    }

    private static Mock<IUserAccessRepository> RepositoryWithRoles(
        IReadOnlyList<Role> roles, IReadOnlyList<Permission>? existingPermissions = null)
    {
        var repo = new Mock<IUserAccessRepository>();
        repo.Setup(x => x.GetRolesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(roles);
        repo.Setup(x => x.GetPermissionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingPermissions ?? (IReadOnlyList<Permission>)[]);
        return repo;
    }

    private static Task Sync(IUserAccessRepository repository) =>
        DiscoveredPermissionSynchronizer.SyncAsync(repository, NullLogger.Instance, typeof(DiscoveredPermissionSynchronizerTests).Assembly);

    // ── New permission → Full Access role gets it (via Step 2 — no explicit allocation needed) ──
    [Fact]
    public async Task A_full_access_custom_role_receives_no_explicit_allocation_for_a_new_permission()
    {
        var fullAccessRole = CustomRole("Healthcare Platform Admin", isFullAccess: true);
        var repo = RepositoryWithRoles([fullAccessRole]);

        await Sync(repo.Object);

        repo.Verify(x => x.AddPermissionAsync(
            It.Is<Permission>(p => p.Id == NewPermissionId && p.Name == NewPermissionCode),
            It.IsAny<CancellationToken>()), Times.Once,
            "the new permission itself must still be created in the catalog");
        repo.Verify(x => x.AddRolePermissionAsync(fullAccessRole.Id, NewPermissionId, It.IsAny<CancellationToken>()), Times.Never,
            "a full-access role gets the new permission through CachedUserPermissionsProvider's live union, not an explicit row");
    }

    // ── New permission → normal custom role does NOT get it ─────────────────────────────
    [Fact]
    public async Task A_normal_custom_role_does_not_receive_the_new_permission()
    {
        var normalRole = CustomRole("Epic Integration Manager", isFullAccess: false);
        var repo = RepositoryWithRoles([normalRole]);

        await Sync(repo.Object);

        repo.Verify(x => x.AddRolePermissionAsync(normalRole.Id, NewPermissionId, It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Existing system roles retain their current behavior ─────────────────────────────
    [Fact]
    public async Task Operations_and_Audit_style_system_roles_still_receive_an_explicit_allocation()
    {
        var operations = SystemRole(Guid.NewGuid(), "Operations", isFullAccess: false);
        var audit = SystemRole(Guid.NewGuid(), "Audit", isFullAccess: false);
        var repo = RepositoryWithRoles([operations, audit]);

        await Sync(repo.Object);

        repo.Verify(x => x.AddRolePermissionAsync(operations.Id, NewPermissionId, It.IsAny<CancellationToken>()), Times.Once,
            "a non-full-access system role's historical 'never lose access' guarantee must be unchanged");
        repo.Verify(x => x.AddRolePermissionAsync(audit.Id, NewPermissionId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SuperAdmin_and_Admin_receive_no_explicit_allocation_because_they_are_already_full_access()
    {
        var superAdmin = SystemRole(SeededSecurityIds.SuperAdminRoleId, "SuperAdmin", isFullAccess: true);
        var admin = SystemRole(SeededSecurityIds.AdminRoleId, "Admin", isFullAccess: true);
        var repo = RepositoryWithRoles([superAdmin, admin]);

        await Sync(repo.Object);

        repo.Verify(x => x.AddRolePermissionAsync(superAdmin.Id, NewPermissionId, It.IsAny<CancellationToken>()), Times.Never,
            "SuperAdmin's effective access to the new permission is unchanged -- it now comes from IsFullAccess, not a row");
        repo.Verify(x => x.AddRolePermissionAsync(admin.Id, NewPermissionId, It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── A mixed roster: only the non-full-access system role gets a row ────────────────
    [Fact]
    public async Task Only_the_non_full_access_system_role_gets_a_row_when_all_role_kinds_are_present()
    {
        var superAdmin = SystemRole(SeededSecurityIds.SuperAdminRoleId, "SuperAdmin", isFullAccess: true);
        var operations = SystemRole(Guid.NewGuid(), "Operations", isFullAccess: false);
        var fullAccessCustom = CustomRole("Healthcare Platform Admin", isFullAccess: true);
        var normalCustom = CustomRole("Epic Integration Manager", isFullAccess: false);
        var repo = RepositoryWithRoles([superAdmin, operations, fullAccessCustom, normalCustom]);

        await Sync(repo.Object);

        repo.Verify(x => x.AddRolePermissionAsync(operations.Id, NewPermissionId, It.IsAny<CancellationToken>()), Times.Once);
        repo.Verify(x => x.AddRolePermissionAsync(It.IsAny<Guid>(), NewPermissionId, It.IsAny<CancellationToken>()), Times.Once,
            "exactly one role (Operations) should have received an explicit allocation");
    }

    // ── Existing permission allocations remain unchanged ────────────────────────────────
    [Fact]
    public async Task An_already_existing_permission_never_re_enters_the_grant_loop()
    {
        var existing = new Permission(NewPermissionId, NewPermissionCode, "Sample: Assign", "Already exists.", groupId: null);
        var anyRole = CustomRole("Whatever Role", isFullAccess: false);
        var repo = RepositoryWithRoles([anyRole], existingPermissions: [existing]);

        await Sync(repo.Object);

        repo.Verify(x => x.AddPermissionAsync(It.IsAny<Permission>(), It.IsAny<CancellationToken>()), Times.Never,
            "an already-existing permission must never be re-created");
        repo.Verify(x => x.AddRolePermissionAsync(It.IsAny<Guid>(), NewPermissionId, It.IsAny<CancellationToken>()), Times.Never,
            "existing allocations for an existing permission must never be touched by this method");
    }

    // ── Re-running synchronization does not create unwanted grants ─────────────────────
    [Fact]
    public async Task Re_running_synchronization_after_the_permission_now_exists_grants_nothing_further()
    {
        var operations = SystemRole(Guid.NewGuid(), "Operations", isFullAccess: false);
        var normalCustom = CustomRole("Epic Integration Manager", isFullAccess: false);

        // First run: permission doesn't exist yet.
        var firstRunRepo = RepositoryWithRoles([operations, normalCustom]);
        await Sync(firstRunRepo.Object);
        firstRunRepo.Verify(x => x.AddPermissionAsync(It.IsAny<Permission>(), It.IsAny<CancellationToken>()), Times.Once);
        firstRunRepo.Verify(x => x.AddRolePermissionAsync(operations.Id, NewPermissionId, It.IsAny<CancellationToken>()), Times.Once);

        // Second run (simulating a restart): the permission the first run created now exists, with the
        // exact allocations the first run left it with -- nothing further should ever be granted or
        // re-created, to either role.
        var nowExisting = new Permission(NewPermissionId, NewPermissionCode, "Sample: Assign", "Already exists.", groupId: null);
        var secondRunRepo = RepositoryWithRoles([operations, normalCustom], existingPermissions: [nowExisting]);

        await Sync(secondRunRepo.Object);

        secondRunRepo.Verify(x => x.AddPermissionAsync(It.IsAny<Permission>(), It.IsAny<CancellationToken>()), Times.Never);
        secondRunRepo.Verify(x => x.AddRolePermissionAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}

/// <summary>Test-only controller — see the class-level doc comment on <see cref="DiscoveredPermissionSynchronizerTests"/>.</summary>
[StandardPermission(PermissionGroupCode.Sample, PermissionActionCode.Assign, "Step 5 test-only permission.")]
internal sealed class DiscoveredPermissionSynchronizerTestController : ControllerBase
{
}
