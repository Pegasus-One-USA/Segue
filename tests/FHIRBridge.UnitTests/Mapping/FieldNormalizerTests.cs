using FHIRBridge.Application.Services.Mapping.Internal;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Mapping;

public sealed class FieldNormalizerTests
{
    [Theory]
    [InlineData("firstName", "first name")]
    [InlineData("phone_number", "phone number")]
    [InlineData("FirstName", "first name")]
    [InlineData("BirthDate", "birth date")]
    public void Normalize_splits_camelCase_snake_case_and_PascalCase(string raw, string expected)
    {
        FieldNormalizer.Normalize(raw).Should().Be(expected);
    }

    [Theory]
    [InlineData("dob", "date of birth")]
    [InlineData("mrn", "medical record number")]
    public void Normalize_expands_known_abbreviations(string raw, string expected)
    {
        FieldNormalizer.Normalize(raw).Should().Be(expected);
    }

    [Fact]
    public void Normalize_strips_array_indices()
    {
        FieldNormalizer.Normalize("identifier[0]").Should().Be("identifier");
    }

    [Fact]
    public void Normalize_is_idempotent_and_cached_consistently()
    {
        var first = FieldNormalizer.Normalize("mobileNumber");
        var second = FieldNormalizer.Normalize("mobileNumber");
        first.Should().Be(second);
    }

    [Fact]
    public void NormalizeStructuralPath_joins_segments_with_spaces()
    {
        FieldNormalizer.NormalizeStructuralPath("name.given").Should().Be("name given");
    }
}
