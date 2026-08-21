using FHIRBridge.Application.Services.Mapping.Internal;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Mapping;

public sealed class ValuePatternAnalyzerTests
{
    [Theory]
    [InlineData("1985-04-12", "date")]
    [InlineData("M", "gender")]
    [InlineData("jane.doe@example.com", "email")]
    [InlineData("E11.9", "icd10")]
    [InlineData("2339-0", "loinc")]
    [InlineData("1234567893", "npi")]
    public void DetectPattern_recognizes_known_shapes(string sample, string expectedPattern)
    {
        ValuePatternAnalyzer.DetectPattern(sample).Should().Be(expectedPattern);
    }

    [Fact]
    public void DetectPattern_returns_null_for_unrecognizable_free_text()
    {
        ValuePatternAnalyzer.DetectPattern("John Doe's Pharmacy").Should().BeNull();
    }

    [Fact]
    public void Score_returns_1_when_destination_name_corroborates_the_pattern()
    {
        ValuePatternAnalyzer.Score("1985-04-12", "birth date").Should().Be(1.0);
    }

    [Fact]
    public void Score_returns_partial_credit_when_pattern_detected_but_destination_gives_no_hint()
    {
        ValuePatternAnalyzer.Score("1985-04-12", "unrelated field").Should().Be(0.3);
    }

    [Fact]
    public void Score_returns_0_for_null_or_empty_sample()
    {
        ValuePatternAnalyzer.Score(null, "birth date").Should().Be(0.0);
        ValuePatternAnalyzer.Score("", "birth date").Should().Be(0.0);
    }
}
