using FHIRBridge.Application.Mappings;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Configuration;

/// <summary>
/// A disabled <c>MappingField</c> (user unchecked it in the mapping UI, but it remains on the profile) must not
/// reach the mapping engine — otherwise its data is still extracted and shows up as an unexpected column/value in
/// mapped-only destinations (e.g. CSV). <c>ConfiguredPipelineService.MapResourcesAsync</c> and
/// <c>TransformNodeExecutors.ResolveResourceMappingConfigsAsync</c> both filter on <c>MappingFieldDto.IsEnabled</c>
/// after converting via <c>ConfigurationMapper.ToDto</c> — so that conversion must carry the flag through faithfully.
/// </summary>
public sealed class MappingFieldIsEnabledMappingTests
{
    [Fact]
    public void ToDto_carries_enabled_flag_through()
    {
        var field = new MappingField(
            "Code", "$.code.text", MappingValueType.String, IsRequired: false, DefaultValue: null, Format: null,
            IsEnabled: true);

        var dto = ConfigurationMapper.ToDto(field);

        dto.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public void ToDto_carries_disabled_flag_through()
    {
        var field = new MappingField(
            "Code", "$.code.text", MappingValueType.String, IsRequired: false, DefaultValue: null, Format: null,
            IsEnabled: false);

        var dto = ConfigurationMapper.ToDto(field);

        dto.IsEnabled.Should().BeFalse();
    }
}
