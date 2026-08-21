using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace FHIRBridge.UnitTests.Security;

/// <summary>
/// <see cref="Permission.Source"/> defaults every existing construction path to
/// <see cref="PermissionSource.Code"/> (backward compatible with every permission that exists before
/// this field was added — see the PermissionConfiguration column default for the DB-level equivalent
/// of this same guarantee), and <see cref="Permission.MarkAsCodeManaged"/> is the one-way promotion a
/// migration-defined permission goes through once code starts declaring/discovering it.
/// </summary>
public sealed class PermissionSourceTests
{
    private static Permission NewPermission(PermissionSource source = PermissionSource.Code)
        => new(Guid.NewGuid(), "epic.archive", "Archive Epic", "Archive an Epic source connection.", Guid.NewGuid(), source: source);

    [Fact]
    public void Defaults_to_code_sourced_when_not_specified()
    {
        // Every pre-existing call site (RbacBootstrapper, Program.SyncDiscoveredPermissionsAsync's
        // create branch, InMemoryUserAccessRepository) constructs a Permission without ever
        // mentioning `source` — this default is what keeps all of them correct with zero changes.
        var permission = new Permission(Guid.NewGuid(), "user.view", "View User", "View the list of users.", Guid.NewGuid());

        permission.Source.Should().Be(PermissionSource.Code);
    }

    [Fact]
    public void Can_be_constructed_as_migration_sourced()
    {
        var permission = NewPermission(PermissionSource.Migration);

        permission.Source.Should().Be(PermissionSource.Migration);
    }

    [Fact]
    public void MarkAsCodeManaged_promotes_a_migration_sourced_permission()
    {
        var permission = NewPermission(PermissionSource.Migration);

        permission.MarkAsCodeManaged();

        permission.Source.Should().Be(PermissionSource.Code);
    }

    [Fact]
    public void MarkAsCodeManaged_is_a_no_op_on_an_already_code_sourced_permission()
    {
        var permission = NewPermission(PermissionSource.Code);

        permission.MarkAsCodeManaged();

        permission.Source.Should().Be(PermissionSource.Code);
    }
}
