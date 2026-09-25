using FHIRBridge.Application.Services.Transforms;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Transforms;

/// <summary>
/// Both sides of the SourceField match have to agree on one convention, and the repository compares them with
/// exact string equality — a disagreement resolves NO rule rather than failing loudly. These pin the
/// conversion against the format the portal actually persists (field-mapping-join-popover.component.ts's
/// ruleSourceField, built from MappingRow.sources[].fhirPath or MappingRow.childNodeId).
/// </summary>
public sealed class RuleSourceFieldFormatTests
{
    [Fact]
    public void FromJsonPath_StripsLeadingDollarAndPrefixesResourceType()
    {
        RuleSourceFieldFormat.FromJsonPath("Patient", "$.birthDate").Should().Be("Patient.birthDate");
    }

    [Fact]
    public void FromJsonPath_StripsArrayIndexAnnotations()
    {
        RuleSourceFieldFormat.FromJsonPath("Condition", "$.code.coding[*].code")
            .Should().Be("Condition.code.coding.code");
    }

    /// <summary>
    /// The whole-payload-as-JSON mapping: workflow-build-assembler-v2's toJsonPath returns a bare "$" for the
    /// resource's own root node, while the portal persists that row's SourceField as the bare node id
    /// ("Patient"). Without the empty-remainder branch this produced "Patient." and no rule authored on a
    /// whole-node mapping could ever resolve.
    /// </summary>
    [Fact]
    public void FromJsonPath_WholeDocumentPathIsJustTheResourceType()
    {
        RuleSourceFieldFormat.FromJsonPath("Patient", "$").Should().Be("Patient");
    }

    /// <summary>A whole-NODE (not whole-payload) mapping — MappingRow.childNodeId "Patient.name" becomes
    /// jsonPath "$.name", which must normalize back to exactly the id the portal saved.</summary>
    [Fact]
    public void FromJsonPath_WholeNodePathRoundTripsToTheChildNodeId()
    {
        RuleSourceFieldFormat.FromJsonPath("Patient", "$.name").Should().Be("Patient.name");
    }

    [Fact]
    public void FromJsonPath_NullOrEmptyJsonPathIsNull()
    {
        RuleSourceFieldFormat.FromJsonPath("Patient", null).Should().BeNull();
        RuleSourceFieldFormat.FromJsonPath("Patient", string.Empty).Should().BeNull();
    }
}
