using FHIRBridge.Application.Security;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Infrastructure.Persistence;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Security;

/// <summary>
/// RBAC Fix 10 (Step 7 audit, final code fix): InMemoryUserAccessRepository's own built-in-role seeding
/// (its <c>_roles</c> field initializer) must reflect the same Full Access model RbacBootstrapper's real,
/// database-backed fresh-install seeding already establishes -- SuperAdmin, and only SuperAdmin, is
/// created with IsFullAccess=true (via the same Role.SetFullAccess(true) domain API, never a changed
/// constructor default or a blanket "every seeded role" grant). RbacSeedData.Roles today contains only
/// SuperAdmin, so Admin/Operations/Audit are no longer auto-seeded by the real bootstrapper at all -- this
/// in-memory stand-in (used wherever tests substitute IUserAccessRepository) must not grant any of them
/// Full Access either. Exercised directly against the concrete class -- it has no constructor
/// dependencies -- never a mock, since the seeding under test lives in its own field initializers.
/// </summary>
public sealed class InMemoryUserAccessRepositoryFullAccessTests
{
    // ── SuperAdmin is seeded Full Access ─────────────────────────────────────────────────
    [Fact]
    public async Task Seeded_SuperAdmin_has_IsFullAccess_true()
    {
        var repository = new InMemoryUserAccessRepository(TestHelpers.LicenseTestScopeFactory.Create());

        var superAdmin = await repository.GetRoleByIdAsync(SeededSecurityIds.SuperAdminRoleId, CancellationToken.None);

        superAdmin.Should().NotBeNull();
        superAdmin!.IsFullAccess.Should().BeTrue();
        superAdmin.IsSystem.Should().BeTrue();
        superAdmin.Name.Should().Be(UnifiedRoles.SuperAdmin);
    }

    // ── Admin matches the real bootstrapper's current behavior: NOT Full Access ─────────
    // RbacBootstrapper.EnsureAsync only calls SetFullAccess(true) when seed.Id ==
    // SeededSecurityIds.SuperAdminRoleId (RbacSeedData.Roles contains only the SuperAdmin seed entry
    // today -- Admin is no longer auto-created by the real bootstrapper at all). This in-memory
    // repository must therefore match that exactly: Admin stays IsFullAccess=false here, same as the
    // real bootstrapper would leave any hypothetical fresh Admin row.
    [Fact]
    public async Task Seeded_Admin_has_IsFullAccess_false_matching_the_real_bootstrappers_current_behavior()
    {
        var repository = new InMemoryUserAccessRepository(TestHelpers.LicenseTestScopeFactory.Create());

        var admin = await repository.GetRoleByIdAsync(SeededSecurityIds.AdminRoleId, CancellationToken.None);

        admin.Should().NotBeNull();
        admin!.IsFullAccess.Should().BeFalse();
        admin.IsSystem.Should().BeTrue();
        admin.Name.Should().Be(UnifiedRoles.Admin);
    }

    // ── Operations remains non-Full-Access ───────────────────────────────────────────────
    [Fact]
    public async Task Seeded_Operations_remains_IsFullAccess_false()
    {
        var repository = new InMemoryUserAccessRepository(TestHelpers.LicenseTestScopeFactory.Create());

        var operations = await repository.GetRoleByIdAsync(SeededSecurityIds.OperationsRoleId, CancellationToken.None);

        operations.Should().NotBeNull();
        operations!.IsFullAccess.Should().BeFalse();
    }

    // ── Audit remains non-Full-Access ────────────────────────────────────────────────────
    [Fact]
    public async Task Seeded_Audit_remains_IsFullAccess_false()
    {
        var repository = new InMemoryUserAccessRepository(TestHelpers.LicenseTestScopeFactory.Create());

        var audit = await repository.GetRoleByIdAsync(SeededSecurityIds.AuditRoleId, CancellationToken.None);

        audit.Should().NotBeNull();
        audit!.IsFullAccess.Should().BeFalse();
    }

    // ── A normal/custom role added later is never granted Full Access by this repository ─
    [Fact]
    public async Task A_normal_custom_role_remains_IsFullAccess_false()
    {
        var repository = new InMemoryUserAccessRepository(TestHelpers.LicenseTestScopeFactory.Create());
        var customRole = new Role(Guid.NewGuid(), "Epic Integration Manager", "A scoped custom role.", isSystem: false);

        await repository.AddRoleAsync(customRole, CancellationToken.None);
        var stored = await repository.GetRoleByIdAsync(customRole.Id, CancellationToken.None);

        stored.Should().NotBeNull();
        stored!.IsFullAccess.Should().BeFalse();
    }

    // ── All four built-in roles, read back together, match the intended model exactly ───
    [Fact]
    public async Task GetRolesAsync_reflects_exactly_one_Full_Access_built_in_role()
    {
        var repository = new InMemoryUserAccessRepository(TestHelpers.LicenseTestScopeFactory.Create());

        var roles = await repository.GetRolesAsync(CancellationToken.None);

        roles.Where(r => r.IsFullAccess).Select(r => r.Name).Should().BeEquivalentTo(new[] { UnifiedRoles.SuperAdmin });
    }
}
