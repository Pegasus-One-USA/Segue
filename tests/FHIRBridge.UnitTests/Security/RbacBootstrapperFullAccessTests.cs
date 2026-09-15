using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Security;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Infrastructure.Persistence;
using FHIRBridge.Infrastructure.Security;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FHIRBridge.UnitTests.Security;

/// <summary>
/// RBAC Fix 1 (Step 7 audit, high-severity): on a brand-new database, RbacBootstrapper.EnsureAsync is the
/// ONLY place the seeded SuperAdmin role row is ever created (migrations run first, so the
/// AddRoleIsFullAccess migration's own raw-SQL backfill finds an empty Roles table and updates zero rows).
/// Without explicitly setting IsFullAccess=true here, a fresh install would leave SuperAdmin's own
/// IsFullAccess at the entity constructor's default of false forever, and since
/// RoleManagementService.CallerHasFullAccessAsync checks IsFullAccess only (no role-name fallback), nobody
/// -- not even SuperAdmin -- could ever grant/revoke Full System Access on any role. Exercised here against
/// a real FHIRBridgeDbContext backed by the EF Core InMemory provider (same pattern as
/// DestinationConfigurationSoftDeleteTests/WorkflowSqlStoreTests), not a mock, since RbacBootstrapper takes
/// the concrete DbContext directly.
/// </summary>
public sealed class RbacBootstrapperFullAccessTests
{
    // Shared root, per-test unique database name -- same reasoning as DestinationConfigurationSoftDeleteTests:
    // avoids EF's ManyServiceProvidersCreatedWarning while still giving each test full isolation.
    private static readonly InMemoryDatabaseRoot _root = new();
    private readonly string _databaseName = Guid.NewGuid().ToString();

    private FHIRBridgeDbContext CreateContext()
    {
        // AuditableChildEntity's CreatedBy is a required column, normally stamped by this interceptor from
        // the ambient ICurrentUserService — RbacBootstrapper itself never sets it explicitly. Same pattern
        // as DestinationConfigurationSoftDeleteTests.
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(x => x.CurrentUser)
            .Returns(new CurrentUserInfo("system", "system@test.local", "System", ["SuperAdmin"], true));

        var options = new DbContextOptionsBuilder<FHIRBridgeDbContext>()
            .UseInMemoryDatabase(_databaseName, _root)
            .AddInterceptors(new AuditingSaveChangesInterceptor(currentUser.Object))
            .Options;

        return new FHIRBridgeDbContext(options);
    }

    private static RbacBootstrapper CreateBootstrapper(FHIRBridgeDbContext context) =>
        new(context, NullLogger<RbacBootstrapper>.Instance);

    // ── Fresh bootstrap creates SuperAdmin with IsFullAccess = true ─────────────────────
    [Fact]
    public async Task Fresh_bootstrap_creates_SuperAdmin_with_IsFullAccess_true()
    {
        await using (var context = CreateContext())
        {
            await CreateBootstrapper(context).EnsureAsync(CancellationToken.None);
        }

        await using (var context = CreateContext())
        {
            var superAdmin = await context.Roles
                .IgnoreQueryFilters()
                .SingleAsync(r => r.Id == SeededSecurityIds.SuperAdminRoleId);

            superAdmin.IsFullAccess.Should().BeTrue();
            superAdmin.IsSystem.Should().BeTrue();
            superAdmin.Name.Should().Be(UnifiedRoles.SuperAdmin);
        }
    }

    // ── Fresh bootstrap still grants SuperAdmin's expected permission allocations ───────
    [Fact]
    public async Task Fresh_bootstrap_still_grants_SuperAdmins_seeded_permission_allocations()
    {
        await using (var context = CreateContext())
        {
            await CreateBootstrapper(context).EnsureAsync(CancellationToken.None);
        }

        await using (var context = CreateContext())
        {
            var allocationCount = await context.PermissionAllocations
                .IgnoreQueryFilters()
                .CountAsync(a => a.RoleId == SeededSecurityIds.SuperAdminRoleId);

            var expectedCount = RbacSeedData.RolePermissions[SeededSecurityIds.SuperAdminRoleId].Count;

            // The IsFullAccess fix touches only the Role entity's own flag -- it must not have any effect
            // (fewer or extra rows) on the separate, pre-existing PermissionAllocation-seeding logic further
            // down in the same method.
            allocationCount.Should().Be(expectedCount);
        }
    }

    // ── Existing (already-upgraded) database behavior remains unchanged ─────────────────
    [Fact]
    public async Task An_already_existing_SuperAdmin_role_is_never_touched_or_reset_by_a_later_boot()
    {
        var preExistingRoleId = SeededSecurityIds.SuperAdminRoleId;

        // Simulate an already-upgraded database: the migration's own backfill already correctly set
        // IsFullAccess=true on a pre-existing row, created independently of this bootstrapper.
        await using (var context = CreateContext())
        {
            var preExisting = new Role(preExistingRoleId, UnifiedRoles.SuperAdmin, "Pre-existing description.", isSystem: true);
            preExisting.SetFullAccess(true);
            context.Roles.Add(preExisting);
            await context.SaveChangesAsync();
        }

        await using (var context = CreateContext())
        {
            await CreateBootstrapper(context).EnsureAsync(CancellationToken.None);
        }

        await using (var context = CreateContext())
        {
            var roles = await context.Roles.IgnoreQueryFilters().Where(r => r.Id == preExistingRoleId).ToListAsync();

            roles.Should().HaveCount(1, "the bootstrapper's roleIdSet guard must skip an already-existing role entirely, never duplicating it");
            roles[0].Description.Should().Be("Pre-existing description.", "an existing role's own fields must never be overwritten by this bootstrapper");
            roles[0].IsFullAccess.Should().BeTrue("the pre-existing row's own value must be left exactly as it was");
        }
    }

    // ── Unrelated, non-full-access custom roles are never touched ──────────────────────
    [Fact]
    public async Task An_unrelated_pre_existing_custom_role_is_completely_unaffected()
    {
        var customRoleId = Guid.NewGuid();

        await using (var context = CreateContext())
        {
            var custom = new Role(customRoleId, "Epic Integration Manager", "A scoped custom role.", isSystem: false);
            context.Roles.Add(custom);
            await context.SaveChangesAsync();
        }

        await using (var context = CreateContext())
        {
            await CreateBootstrapper(context).EnsureAsync(CancellationToken.None);
        }

        await using (var context = CreateContext())
        {
            var custom = await context.Roles.IgnoreQueryFilters().SingleAsync(r => r.Id == customRoleId);

            custom.IsFullAccess.Should().BeFalse("the fix is scoped to SeededSecurityIds.SuperAdminRoleId only -- an unrelated custom role must never be granted Full Access");
            custom.IsSystem.Should().BeFalse();
        }
    }

    // ── No duplicate or unintended role changes on a second boot ────────────────────────
    [Fact]
    public async Task Running_EnsureAsync_twice_creates_no_duplicates_and_keeps_IsFullAccess_true()
    {
        await using (var context = CreateContext())
        {
            await CreateBootstrapper(context).EnsureAsync(CancellationToken.None);
        }

        // Second boot -- SuperAdmin's role row now already exists, so this run must hit the
        // roleIdSet.Contains(seed.Id) "continue" branch, never re-adding it or re-running SetFullAccess.
        await using (var context = CreateContext())
        {
            await CreateBootstrapper(context).EnsureAsync(CancellationToken.None);
        }

        await using (var context = CreateContext())
        {
            var roles = await context.Roles.IgnoreQueryFilters().Where(r => r.Id == SeededSecurityIds.SuperAdminRoleId).ToListAsync();
            roles.Should().HaveCount(1);
            roles[0].IsFullAccess.Should().BeTrue();

            var allocationCount = await context.PermissionAllocations
                .IgnoreQueryFilters()
                .CountAsync(a => a.RoleId == SeededSecurityIds.SuperAdminRoleId);
            var expectedCount = RbacSeedData.RolePermissions[SeededSecurityIds.SuperAdminRoleId].Count;
            allocationCount.Should().Be(expectedCount, "a second boot must not create duplicate PermissionAllocation rows");
        }
    }
}
