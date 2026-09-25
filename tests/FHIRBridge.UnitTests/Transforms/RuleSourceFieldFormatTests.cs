using FHIRBridge.Application.Services.Transforms;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Transforms;

/// <summary>
/// The rule repository matches SourceField by exact string comparison, so this converter and the portal's
/// rule-authoring UI must agree on one convention or a rule silently resolves to nothing.
/// </summary>
public sealed class RuleSourceFieldFormatTests
{
    [Theory]
    [InlineData("$.birthDate", "Patient.birthDate")]
    [InlineData("$.name[*].given[*]", "Patient.name.given")]
    [InlineData("name.family", "Patient.name.family")]
    public void Normalizes_a_single_source_path(string jsonPath, string expected)
    {
        RuleSourceFieldFormat.FromJsonPath("Patient", jsonPath).Should().Be(expected);
    }

    [Theory]
    // The UI saves a joined column's rule against sources[0].fhirPath only, so the first sub-path is the key.
    [InlineData("$.name[*].given|$.name[*].family", "Patient.name.given")]
    [InlineData("$.name[*].family|$.name[*].given[*]", "Patient.name.family")]
    [InlineData("$.id|$.name[*].family", "Patient.id")]
    public void Keys_a_joined_path_off_its_first_sub_path(string jsonPath, string expected)
    {
        // Normalizing the whole string produced "Patient.name.given|$.name.family" — a key no rule can match,
        // so every rule on a joined column was filtered out and the column wrote its raw value.
        RuleSourceFieldFormat.FromJsonPath("Patient", jsonPath).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("|")]
    public void Returns_null_when_there_is_no_usable_path(string? jsonPath)
    {
        RuleSourceFieldFormat.FromJsonPath("Patient", jsonPath).Should().BeNull();
    }
}
