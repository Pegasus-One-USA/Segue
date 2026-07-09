using FHIRBridge.Application.Security;
using FluentAssertions;
using Xunit;

namespace FHIRBridge.UnitTests.Security;

/// <summary>
/// <see cref="RbacDefinitionValidator"/>'s generic building blocks take a delegate rather than reflecting
/// over a real enum's attributes, so each duplicate scenario is exercised here against small,
/// private, deliberately-broken test enums -- never the real <see cref="PermissionCategoryCode"/>/
/// <see cref="PermissionGroupCode"/>/<see cref="PermissionActionCode"/> enums, which stay untouched.
/// </summary>
public sealed class RbacDefinitionValidatorTests
{
    private enum DuplicateValueEnum
    {
        First = 1,
        Second = 1, // same underlying value as First
        Third = 2,
    }

    private enum UniqueValueEnum
    {
        First = 1,
        Second = 2,
    }

    [Fact]
    public void ValidateEnumValues_flags_two_members_sharing_the_same_underlying_value()
    {
        var errors = new List<string>();

        RbacDefinitionValidator.ValidateEnumValues<DuplicateValueEnum>(errors);

        errors.Should().ContainSingle()
            .Which.Should().Be("DuplicateValueEnum has duplicate underlying value 1 shared by: First, Second");
    }

    [Fact]
    public void ValidateEnumValues_passes_when_every_value_is_distinct()
    {
        var errors = new List<string>();

        RbacDefinitionValidator.ValidateEnumValues<UniqueValueEnum>(errors);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void ValidateDisplayNames_flags_two_members_with_the_same_display_name_case_insensitively()
    {
        var errors = new List<string>();
        var displayNames = new Dictionary<UniqueValueEnum, string>
        {
            [UniqueValueEnum.First] = "Epic",
            [UniqueValueEnum.Second] = "epic",
        };

        RbacDefinitionValidator.ValidateDisplayNames<UniqueValueEnum>(errors, v => displayNames[v]);

        errors.Should().ContainSingle().Which.Should().Contain("duplicate display name \"Epic\"");
    }

    [Fact]
    public void ValidateDisplayNames_passes_when_every_display_name_is_distinct()
    {
        var errors = new List<string>();
        var displayNames = new Dictionary<UniqueValueEnum, string>
        {
            [UniqueValueEnum.First] = "Epic",
            [UniqueValueEnum.Second] = "Cerner",
        };

        RbacDefinitionValidator.ValidateDisplayNames<UniqueValueEnum>(errors, v => displayNames[v]);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void ValidateIds_flags_two_members_sharing_the_same_declared_id()
    {
        var errors = new List<string>();
        var sharedId = Guid.NewGuid();
        var ids = new Dictionary<UniqueValueEnum, Guid>
        {
            [UniqueValueEnum.First] = sharedId,
            [UniqueValueEnum.Second] = sharedId,
        };

        RbacDefinitionValidator.ValidateIds<UniqueValueEnum>(errors, v => ids[v]);

        errors.Should().ContainSingle().Which.Should().Contain($"duplicate Id {sharedId}");
    }

    [Fact]
    public void ValidateIds_passes_when_every_id_is_distinct()
    {
        var errors = new List<string>();
        var ids = new Dictionary<UniqueValueEnum, Guid>
        {
            [UniqueValueEnum.First] = Guid.NewGuid(),
            [UniqueValueEnum.Second] = Guid.NewGuid(),
        };

        RbacDefinitionValidator.ValidateIds<UniqueValueEnum>(errors, v => ids[v]);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void ValidatePermissionSeeds_flags_two_entries_declaring_the_same_group_and_action()
    {
        var errors = new List<string>();
        var seeds = new[]
        {
            new RbacSeedData.PermissionSeed("View Epic.", PermissionGroupCode.Epic, PermissionActionCode.Read),
            new RbacSeedData.PermissionSeed("View Epic again.", PermissionGroupCode.Epic, PermissionActionCode.Read),
        };

        RbacDefinitionValidator.ValidatePermissionSeeds(errors, seeds);

        errors.Should().ContainSingle().Which.Should().Contain("epic.read");
    }

    [Fact]
    public void ValidatePermissionSeeds_passes_when_every_group_action_pair_is_distinct()
    {
        var errors = new List<string>();
        var seeds = new[]
        {
            new RbacSeedData.PermissionSeed("View Epic.", PermissionGroupCode.Epic, PermissionActionCode.Read),
            new RbacSeedData.PermissionSeed("Edit Epic.", PermissionGroupCode.Epic, PermissionActionCode.Edit),
        };

        RbacDefinitionValidator.ValidatePermissionSeeds(errors, seeds);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_reports_no_errors_for_the_real_taxonomy()
    {
        // Live regression guard: today's actual PermissionCategoryCode/PermissionGroupCode/
        // PermissionActionCode/RbacSeedData.Permissions definitions must already be duplicate-free.
        RbacDefinitionValidator.Validate().Should().BeEmpty();
    }
}
