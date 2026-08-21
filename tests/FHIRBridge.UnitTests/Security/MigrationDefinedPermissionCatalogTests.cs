using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Infrastructure.Persistence;
using FluentAssertions;
using Moq;
using Xunit;

namespace FHIRBridge.UnitTests.Security;

/// <summary>
/// Exercises the migration-defined permission pathway (see <see cref="PermissionSource"/>,
/// <c>PermissionSyncPolicy</c>, and migration <c>AddEpicArchivePermission</c>) at the catalog-service
/// boundary — <see cref="RoleManagementService.GetPermissionCatalogAsync"/> directly, against a real
/// <see cref="InMemoryUserAccessRepository"/>, bypassing HTTP/auth entirely (deliberately: this repo's
/// current auth flow requires MFA completion even for a freshly-created SuperAdmin, which is unrelated
/// machinery this test has no need to drive just to prove a catalog-service claim).
///
/// <see cref="PermissionSyncPolicyTests"/> unit-tests the underlying deactivation rule in isolation;
/// this test proves the other half of the same claim — that a permission which reached the data store
/// by ANY means (not just <c>[StandardPermission]</c>/<c>RbacSeedData</c>) is exposed by the catalog
/// exactly like any other, with zero code change to <see cref="RoleManagementService"/> needed.
/// </summary>
public sealed class MigrationDefinedPermissionCatalogTests
{
    [Fact]
    public async Task Catalog_exposes_a_permission_that_was_never_declared_or_discovered_in_code()
    {
        var repository = new InMemoryUserAccessRepository();

        // Simulates what the hand-authored AddEpicArchivePermission migration does against the real
        // database — insert a Permission row nothing in code declares or discovers, tagged
        // PermissionSource.Migration. Uses the exact same (Group, Action) pair and Id as that migration.
        var epicGroup = (await repository.GetPermissionGroupsAsync(CancellationToken.None))
            .Single(g => g.Name == "Epic");

        var migrationDefinedPermission = new Permission(
            id: Guid.Parse("00000010-0000-0000-0000-000000000013"), // (Epic, Archive) — see AddEpicArchivePermission
            name: "epic.archive",
            displayName: "Archive Epic",
            description: "Archive an Epic source connection.",
            groupId: epicGroup.Id,
            isSystem: false,
            source: PermissionSource.Migration);

        await repository.AddPermissionAsync(migrationDefinedPermission, CancellationToken.None);

        var service = new RoleManagementService(
            repository,
            Mock.Of<ICurrentUserService>(),
            new PassthroughUserDisplayNameResolver());

        var catalog = await service.GetPermissionCatalogAsync(CancellationToken.None);

        // The catalog service has no awareness of "epic.archive" at all — it surfaces this purely
        // because a Permission row exists, is active and visible, and belongs to a visible group. No
        // code change to GetPermissionCatalogAsync (or the controller/DTOs above it) was needed.
        var epicPermissionNames = catalog
            .SelectMany(category => category.Groups)
            .Where(group => group.DisplayName == "Epic")
            .SelectMany(group => group.Permissions)
            .Select(permission => permission.Name)
            .ToArray();

        epicPermissionNames.Should().Contain("epic.archive");
    }
}
