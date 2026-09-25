using FHIRBridge.Application.Services.Transforms;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Transforms;

/// <summary>Covers the sibling hints the Runtime pipeline feeds transform nodes from the source resource's own
/// JSON — the mechanism that lets QuantityRangeAssembly keep an Observation's real unit instead of emitting an
/// empty one, and CodeableConceptBuilder keep the source's own display text.</summary>
public sealed class FhirSourceJsonPatcherSiblingTests
{
    private const string ObservationJson = """
    {
      "resourceType": "Observation",
      "code": { "coding": [ { "system": "http://loinc.org", "code": "2093-3", "display": "Cholesterol [Mass/volume] in Serum or Plasma" } ] },
      "valueQuantity": { "value": 187, "unit": "mg/dL", "system": "http://unitsofmeasure.org", "code": "mg/dL" },
      "referenceRange": [ { "low": { "value": 10, "code": "mg/dL" }, "text": "<=200" } ]
    }
    """;

    [Theory]
    [InlineData("$.valueQuantity.value")]
    [InlineData("valueQuantity.value")]
    [InlineData("$.valueQuantity[0].value")]
    public void TryReadSiblingQuantityUnit_reads_the_unit_next_to_a_value_leaf(string sourceFieldPath)
    {
        FhirSourceJsonPatcher.TryReadSiblingQuantityUnit(ObservationJson, sourceFieldPath)
            .Should().Be("mg/dL");
    }

    [Fact]
    public void TryReadSiblingQuantityUnit_follows_an_explicit_array_index()
    {
        // No "unit" on referenceRange[0].low — the UCUM "code" is the documented fallback.
        FhirSourceJsonPatcher.TryReadSiblingQuantityUnit(ObservationJson, "$.referenceRange[0].low.value")
            .Should().Be("mg/dL");
    }

    [Theory]
    [InlineData("$.referenceRange[0].text")]
    [InlineData("$.code.coding.code")]
    [InlineData(null)]
    [InlineData("")]
    public void TryReadSiblingQuantityUnit_returns_null_for_a_path_that_is_not_a_quantity_value(string? sourceFieldPath)
    {
        FhirSourceJsonPatcher.TryReadSiblingQuantityUnit(ObservationJson, sourceFieldPath).Should().BeNull();
    }

    [Fact]
    public void TryReadSiblingQuantityUnit_returns_null_rather_than_throwing_on_unparseable_json()
    {
        FhirSourceJsonPatcher.TryReadSiblingQuantityUnit("{not json", "$.valueQuantity.value").Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryReadSiblingQuantityUnit_returns_null_rather_than_throwing_on_an_absent_document(string? sourceJson)
    {
        // The parameter is string? and the contract is "null when there's nothing to offer" — a missing
        // document is the emptiest such case. It needs its own assertion because an absent document does NOT
        // fail the same way unparseable text does: JsonNode.Parse(null) throws ArgumentNullException, which a
        // catch (JsonException) never sees, so the exception escapes and fails the whole transform for the
        // resource instead of skipping one optional hint.
        FhirSourceJsonPatcher.TryReadSiblingQuantityUnit(sourceJson, "$.valueQuantity.value").Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryReadSiblingDisplay_returns_null_rather_than_throwing_on_an_absent_document(string? sourceJson)
    {
        FhirSourceJsonPatcher.TryReadSiblingDisplay(sourceJson, "$.code.coding.code").Should().BeNull();
    }

    [Fact]
    public void TryReadSiblingDisplay_handles_the_JsonPath_prefix_the_mapping_profile_actually_stores()
    {
        // Source fields arrive as "$.code.coding.code", not "code.coding.code" — a reader that only understood
        // the bare form silently returned null for every real rule, which is how the display hint went missing.
        FhirSourceJsonPatcher.TryReadSiblingDisplay(ObservationJson, "$.code.coding.code")
            .Should().Be("Cholesterol [Mass/volume] in Serum or Plasma");
    }

    [Fact]
    public void ApplyPatches_writes_a_comparator_back_unescaped()
    {
        var patched = FhirSourceJsonPatcher.ApplyPatches(ObservationJson, [("valueQuantity.comparator", "<=")]);
        patched.Should().Contain("\"comparator\":\"<=\"").And.NotContain("u003C");
    }
}
