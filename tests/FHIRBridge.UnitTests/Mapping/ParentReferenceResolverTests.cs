using FHIRBridge.Application.Abstractions.Mapping;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services;
using FluentAssertions;
using Moq;

namespace FHIRBridge.UnitTests.Mapping;

/// <summary>
/// <see cref="ParentReferenceResolver"/> is the metadata-driven core of the parent-child mapping
/// feature: given a child and parent resource type, it finds the FHIR reference field on the child that
/// targets the parent, using only <see cref="IFhirElementCatalog"/> data - no resource or field name is
/// ever hardcoded. These tests pin the tie-break rule for the case where more than one field could
/// satisfy the same parent (e.g. a resource with both a "subject" and a "performer" that both allow
/// Patient), and the zero-candidate / override escape-hatch behavior.
/// </summary>
public sealed class ParentReferenceResolverTests
{
    private static FhirElementDto Field(
        string fhirPath,
        IReadOnlyList<string> referenceTargetTypes,
        string cardinality = "0..1") =>
        new(
            Label: fhirPath,
            JsonPath: $"$.{fhirPath}",
            FhirPath: fhirPath,
            Cardinality: cardinality,
            ValueType: "String",
            IsArray: cardinality != "0..1",
            Arrays: [],
            ReferenceTargetTypes: referenceTargetTypes);

    private static ParentReferenceResolver BuildSut(IReadOnlyList<FhirElementDto> childFields)
    {
        var catalog = new Mock<IFhirElementCatalog>();
        catalog.Setup(x => x.Fields("Observation")).Returns(childFields);
        return new ParentReferenceResolver(catalog.Object);
    }

    [Fact]
    public void Resolves_the_single_candidate_field_when_only_one_targets_the_parent()
    {
        var sut = BuildSut([
            Field("subject.reference", ["Patient", "Group"]),
            Field("encounter.reference", ["Encounter"]),
        ]);

        var result = sut.Resolve("Observation", "Patient");

        result.Should().NotBeNull();
        result!.FhirPath.Should().Be("subject.reference");
    }

    [Fact]
    public void Returns_null_when_no_field_can_target_the_parent_resource_type()
    {
        var sut = BuildSut([
            Field("subject.reference", ["Patient", "Group"]),
        ]);

        var result = sut.Resolve("Observation", "Practitioner");

        result.Should().BeNull();
    }

    [Fact]
    public void Tie_break_prefers_the_more_specific_field_when_multiple_candidates_target_the_parent()
    {
        // "subject" allows only Patient|Group (specific); "performer" allows five types including
        // Patient (broad). Neither name is treated specially - only the candidate count decides.
        var sut = BuildSut([
            Field("performer.reference", ["Practitioner", "PractitionerRole", "Organization", "Patient", "RelatedPerson"]),
            Field("subject.reference", ["Patient", "Group"]),
        ]);

        var result = sut.Resolve("Observation", "Patient");

        result!.FhirPath.Should().Be("subject.reference");
    }

    [Fact]
    public void Tie_break_prefers_singular_cardinality_when_specificity_is_equal()
    {
        var sut = BuildSut([
            Field("performer.reference", ["Patient"], cardinality: "0..*"),
            Field("subject.reference", ["Patient"], cardinality: "0..1"),
        ]);

        var result = sut.Resolve("Observation", "Patient");

        result!.FhirPath.Should().Be("subject.reference");
    }

    [Fact]
    public void Tie_break_falls_back_to_alphabetical_fhir_path_as_final_tiebreak()
    {
        var sut = BuildSut([
            Field("zzzTarget.reference", ["Patient"]),
            Field("aaaTarget.reference", ["Patient"]),
        ]);

        var result = sut.Resolve("Observation", "Patient");

        result!.FhirPath.Should().Be("aaaTarget.reference");
    }

    [Fact]
    public void Override_wins_over_auto_resolution_even_when_a_valid_candidate_exists()
    {
        var sut = BuildSut([
            Field("subject.reference", ["Patient"]),
            Field("performer.reference", ["Patient"]),
        ]);

        var result = sut.Resolve("Observation", "Patient", referenceFieldOverride: "performer.reference");

        result!.FhirPath.Should().Be("performer.reference");
    }

    [Fact]
    public void Override_pointing_at_a_nonexistent_field_resolves_to_null()
    {
        var sut = BuildSut([
            Field("subject.reference", ["Patient"]),
        ]);

        var result = sut.Resolve("Observation", "Patient", referenceFieldOverride: "notAField.reference");

        result.Should().BeNull();
    }
}
