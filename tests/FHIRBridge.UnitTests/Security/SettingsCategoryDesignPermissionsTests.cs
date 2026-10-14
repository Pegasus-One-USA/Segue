using System.Reflection;
using FHIRBridge.Api.Security;
using FHIRBridge.Application.Security;
using FHIRBridge.Domain.Enums;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Security;

/// <summary>
/// The Settings permission category is design only: its three groups show every Platform-table column in the
/// User Management grid, but nothing authorizes against them yet. These tests pin both halves — the shape the
/// grid renders, and that no endpoint or vendor lookup has started relying on them by accident.
/// </summary>
public sealed class SettingsCategoryDesignPermissionsTests
{
    private static readonly PermissionGroupCode[] SettingsGroups =
    [
        PermissionGroupCode.Branding,
        PermissionGroupCode.WorkflowConfiguration,
        PermissionGroupCode.SystemSettings,
    ];

    [Fact]
    public void Settings_category_owns_exactly_the_three_design_groups()
    {
        Enum.GetValues<PermissionGroupCode>()
            .Where(g => g.GetCategory() == PermissionCategoryCode.Settings)
            .Should().BeEquivalentTo(SettingsGroups);

        SettingsGroups.Select(g => g.GetDisplayName())
            .Should().Equal("Branding", "Workflow Configuration", "System Setting");
    }

    [Fact]
    public void Each_settings_group_seeds_every_platform_column_action()
    {
        var expectedActions = Enum.GetValues<PermissionActionCode>()
            .Where(a => a != PermissionActionCode.Invite && a != PermissionActionCode.Assign)
            .ToArray();

        foreach (var group in SettingsGroups)
        {
            RbacSeedData.Permissions
                .Where(p => p.Group == group)
                .Select(p => p.Action)
                .Should().BeEquivalentTo(expectedActions, "the {0} row should show every Platform column", group);
        }
    }

    [Fact]
    public void No_endpoint_authorizes_against_a_settings_group_yet()
    {
        var declared = typeof(StandardPermissionAttribute).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .SelectMany(m => m.GetCustomAttributes<StandardPermissionAttribute>())
                .Concat(t.GetCustomAttributes<StandardPermissionAttribute>()))
            .Select(a => a.Group);

        declared.Should().NotContain(SettingsGroups,
            "the Settings category is design only; wiring an endpoint to it changes who has access");
    }

    [Fact]
    public void Settings_groups_never_resolve_as_a_source_or_destination_vendor()
    {
        SourceSystemPermissionGroups.AllGroupsFor(typeof(SourceSystemType))
            .Concat(SourceSystemPermissionGroups.AllGroupsFor(typeof(DestinationType)))
            .Should().NotContain(SettingsGroups);
    }
}
