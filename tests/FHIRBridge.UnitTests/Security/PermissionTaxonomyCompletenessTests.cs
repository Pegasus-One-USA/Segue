using FHIRBridge.Application.Security;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Security;

/// <summary>
/// Guards the permission taxonomy's completeness. Adding a new <see cref="PermissionCategoryCode"/> or
/// <see cref="PermissionGroupCode"/> member compiles fine on its own, but <see cref="StandardPermissionAttribute"/>,
/// <see cref="FHIRBridge.Infrastructure.Security.RbacBootstrapper"/>, and
/// <see cref="FHIRBridge.Infrastructure.Persistence.InMemoryUserAccessRepository"/> all trust
/// <see cref="PermissionTaxonomy.GroupCategory"/> and <see cref="RbacSeedData"/> to already have an entry for it —
/// without one, the first thing to touch that member throws <see cref="KeyNotFoundException"/> at runtime. These
/// tests turn that into an immediate, compulsory build failure instead.
/// </summary>
public sealed class PermissionTaxonomyCompletenessTests
{
    [Fact]
    public void Every_category_has_a_stable_seeded_id()
    {
        var categoryCodes = Enum.GetValues<PermissionCategoryCode>();

        categoryCodes.Should().OnlyContain(
            code => RbacSeedData.CategoryIdsByCode.ContainsKey(code),
            "every PermissionCategoryCode member needs a RbacSeedData.Categories entry with a stable Id " +
            "(add one alongside a new SeededSecurityIds Guid), or it gets a freshly generated, " +
            "non-deterministic-across-environments Id at runtime instead");
    }

    [Fact]
    public void Every_group_has_a_parent_category_mapping()
    {
        var groupCodes = Enum.GetValues<PermissionGroupCode>();

        groupCodes.Should().OnlyContain(
            code => PermissionTaxonomy.GroupCategory.ContainsKey(code),
            "every PermissionGroupCode member needs a PermissionTaxonomy.GroupCategory entry declaring which " +
            "PermissionCategoryCode owns it");
    }

    [Fact]
    public void Every_group_has_a_stable_seeded_id()
    {
        var groupCodes = Enum.GetValues<PermissionGroupCode>();

        groupCodes.Should().OnlyContain(
            code => RbacSeedData.GroupIdsByCode.ContainsKey(code),
            "every PermissionGroupCode member needs a RbacSeedData.Groups entry with a stable Id " +
            "(add one alongside a new SeededSecurityIds Guid), or it gets a freshly generated, " +
            "non-deterministic-across-environments Id at runtime instead");
    }

    [Fact]
    public void Seeded_category_ids_are_unique()
    {
        RbacSeedData.Categories.Select(c => c.Id).Should().OnlyHaveUniqueItems(
            "a copy-pasted seed entry with a forgotten Id change would silently collide with an existing category");
    }

    [Fact]
    public void Seeded_group_ids_are_unique()
    {
        RbacSeedData.Groups.Select(g => g.Id).Should().OnlyHaveUniqueItems(
            "a copy-pasted seed entry with a forgotten Id change would silently collide with an existing group");
    }
}
