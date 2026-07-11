using System.Reflection;
using FHIRBridge.Application.Security;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Security;

/// <summary>
/// Guards the permission taxonomy's completeness. Adding a new <see cref="PermissionCategoryCode"/> or
/// <see cref="PermissionGroupCode"/> member compiles fine on its own even without the matching
/// <see cref="PermissionCategoryAttribute"/>/<see cref="PermissionGroupAttribute"/>, but
/// <see cref="FHIRBridge.Api.Security.StandardPermissionAttribute"/>,
/// <see cref="FHIRBridge.Infrastructure.Security.RbacBootstrapper"/>, and
/// <see cref="FHIRBridge.Infrastructure.Persistence.InMemoryUserAccessRepository"/> all trust every enum
/// member to have one — without it, the first thing to touch that member throws
/// <see cref="InvalidOperationException"/> at runtime. These tests turn that into an immediate, compulsory
/// build failure instead.
/// </summary>
public sealed class PermissionTaxonomyCompletenessTests
{
    [Fact]
    public void Every_category_has_a_category_attribute()
    {
        var categoryCodes = Enum.GetValues<PermissionCategoryCode>();

        categoryCodes.Should().OnlyContain(
            code => GetCategoryAttribute(code) != null,
            "every PermissionCategoryCode member needs a [PermissionCategory(id, displayName)] attribute " +
            "declaring its stable Id and display name");
    }

    [Fact]
    public void Every_group_has_a_group_attribute()
    {
        var groupCodes = Enum.GetValues<PermissionGroupCode>();

        groupCodes.Should().OnlyContain(
            code => GetGroupAttribute(code) != null,
            "every PermissionGroupCode member needs a [PermissionGroup(id, category, displayName)] attribute " +
            "declaring its stable Id, owning category, and display name");
    }

    [Fact]
    public void Seeded_category_ids_are_unique()
    {
        RbacSeedData.Categories.Select(c => c.Id).Should().OnlyHaveUniqueItems(
            "a copy-pasted [PermissionCategory] with a forgotten Id change would silently collide with an existing category");
    }

    [Fact]
    public void Seeded_group_ids_are_unique()
    {
        RbacSeedData.Groups.Select(g => g.Id).Should().OnlyHaveUniqueItems(
            "a copy-pasted [PermissionGroup] with a forgotten Id change would silently collide with an existing group");
    }

    [Fact]
    public void Seeded_permission_ids_are_unique()
    {
        // Unlike Category/Group ids, a permission's id is a packed encoding of its own (Group, Action) pair
        // rather than a literal declared alongside it, so nothing stops two RbacSeedData.Permissions entries
        // from accidentally repeating the same (Group, Action) pair — this catches that at build time instead
        // of at a live-database unique-index violation.
        RbacSeedData.Permissions.Select(p => p.Id).Should().OnlyHaveUniqueItems(
            "two RbacSeedData.Permissions entries share the same (Group, Action) pair, so they derive the same Id");
    }

    private static PermissionCategoryAttribute? GetCategoryAttribute(PermissionCategoryCode category)
    {
        return typeof(PermissionCategoryCode).GetField(category.ToString())!.GetCustomAttribute<PermissionCategoryAttribute>();
    }

    private static PermissionGroupAttribute? GetGroupAttribute(PermissionGroupCode group)
    {
        return typeof(PermissionGroupCode).GetField(group.ToString())!.GetCustomAttribute<PermissionGroupAttribute>();
    }
}
