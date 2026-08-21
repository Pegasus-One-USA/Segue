using FHIRBridge.Application.Security;
using FHIRBridge.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace FHIRBridge.UnitTests.Security;

/// <summary>
/// <see cref="PermissionSyncPolicy.ShouldDeactivateOrphan"/> is the one piece of
/// <c>Program.SyncDiscoveredPermissionsAsync</c> with real correctness risk — see its own doc comment.
/// Exercised directly here since the method it's pulled out of is a local function inside top-level
/// <c>Program.cs</c> and can't itself be unit tested.
/// </summary>
public sealed class PermissionSyncPolicyTests
{
    [Fact]
    public void Deactivates_a_code_sourced_permission_no_longer_declared_or_discovered()
    {
        PermissionSyncPolicy.ShouldDeactivateOrphan(
            isActive: true,
            source: PermissionSource.Code,
            isSeedDeclared: false,
            isDiscoveredThisBoot: false
        ).Should().BeTrue();
    }

    [Fact]
    public void Never_deactivates_a_migration_sourced_permission_even_when_undiscovered()
    {
        // The exact scenario this whole mechanism exists for: epic.archive, introduced via a
        // hand-authored EF migration ahead of any endpoint enforcing it, must survive every boot
        // until code starts declaring/discovering it — see PermissionSource's own doc comment.
        PermissionSyncPolicy.ShouldDeactivateOrphan(
            isActive: true,
            source: PermissionSource.Migration,
            isSeedDeclared: false,
            isDiscoveredThisBoot: false
        ).Should().BeFalse();
    }

    [Fact]
    public void Never_deactivates_an_already_inactive_permission()
    {
        PermissionSyncPolicy.ShouldDeactivateOrphan(
            isActive: false,
            source: PermissionSource.Code,
            isSeedDeclared: false,
            isDiscoveredThisBoot: false
        ).Should().BeFalse();
    }

    [Fact]
    public void Never_deactivates_a_seed_declared_permission_even_if_code_sourced()
    {
        // RbacBootstrapper's territory, not this method's — mirrors the existing seedDeclaredPermissionIds
        // guard in SyncDiscoveredPermissionsAsync.
        PermissionSyncPolicy.ShouldDeactivateOrphan(
            isActive: true,
            source: PermissionSource.Code,
            isSeedDeclared: true,
            isDiscoveredThisBoot: false
        ).Should().BeFalse();
    }

    [Fact]
    public void Never_deactivates_a_permission_still_discovered_this_boot()
    {
        PermissionSyncPolicy.ShouldDeactivateOrphan(
            isActive: true,
            source: PermissionSource.Code,
            isSeedDeclared: false,
            isDiscoveredThisBoot: true
        ).Should().BeFalse();
    }
}
