using FHIRBridge.Application.Services.Mapping.Internal;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Mapping;

public sealed class SimilarityEngineTests
{
    [Fact]
    public void JaroWinkler_returns_1_for_identical_strings()
    {
        SimilarityEngine.JaroWinkler("birth date", "birth date").Should().Be(1.0);
    }

    [Fact]
    public void JaroWinkler_returns_0_when_either_string_is_empty()
    {
        SimilarityEngine.JaroWinkler("", "birth date").Should().Be(0.0);
        SimilarityEngine.JaroWinkler("birth date", "").Should().Be(0.0);
    }

    [Theory]
    [InlineData("first name", "firstname")]
    [InlineData("mobile number", "mobile")]
    public void JaroWinkler_scores_close_variants_highly(string a, string b)
    {
        SimilarityEngine.JaroWinkler(a, b).Should().BeGreaterThan(0.8);
    }

    [Fact]
    public void JaroWinkler_scores_unrelated_strings_lowly()
    {
        SimilarityEngine.JaroWinkler("birth date", "insurance carrier").Should().BeLessThan(0.6);
    }

    [Fact]
    public void StructuralScore_recognizes_known_path_pattern()
    {
        SimilarityEngine.StructuralScore("name given", "given name").Should().BeGreaterThan(0.9);
    }

    [Fact]
    public void StructuralScore_returns_0_for_unknown_path()
    {
        SimilarityEngine.StructuralScore("some unmapped path", "given name").Should().Be(0.0);
    }
}
