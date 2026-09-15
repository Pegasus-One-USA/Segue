using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Enums;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Mapping;

/// <summary>
/// A FHIR <c>Reference.reference</c> resolves to a "Type/id" pointer, but a destination column wants the id
/// alone: the type prefix is redundant (the column already says what it points at) and it breaks any join
/// against the referenced resource's own id column, which stores the bare value.
///
/// Regression: only fields carrying a ReferenceLookup (the mapping screen's "Resolves to" control) were
/// stripped, via ExtractReferenceId. A plain mapping of the same path stored the raw pointer — which is why
/// Observation.EncounterId held "Encounter/anon-4945b59e9f1f3d59" while Encounter.PatientId, which did have
/// "Resolves to" set, correctly held the bare id.
/// </summary>
public sealed class JsonMappingEngineReferenceIdTests
{
    private static readonly JsonMappingEngine Sut = new();

    private static MappingFieldDto Field(string target, string jsonPath) =>
        new(target, jsonPath, MappingValueType.String, IsRequired: false, DefaultValue: null, Format: null);

    private const string ObservationJson = """
        {
          "resourceType": "Observation",
          "id": "obs-1",
          "subject": { "reference": "Patient/anon-cMg3", "display": "Jane Doe" },
          "encounter": { "reference": "Encounter/anon-4945b59e9f1f3d59" },
          "status": "final"
        }
        """;

    [Fact]
    public void Reference_fields_store_only_the_id()
    {
        var fields = new[]
        {
            Field("PatientId", "$.subject.reference"),
            Field("EncounterId", "$.encounter.reference"),
        };

        var result = Sut.Map(ObservationJson, fields);

        result.Values["PatientId"].Should().Be("anon-cMg3");
        result.Values["EncounterId"].Should().Be("anon-4945b59e9f1f3d59");
    }

    [Fact]
    public void Non_reference_fields_are_untouched()
    {
        // Guards against over-eager stripping: only a path ending in ".reference" is a FHIR pointer. A display
        // string or a status that happens to contain a slash must survive verbatim.
        var fields = new[]
        {
            Field("SubjectDisplay", "$.subject.display"),
            Field("Status", "$.status"),
            Field("ObservationId", "$.id"),
        };

        var result = Sut.Map(ObservationJson, fields);

        result.Values["SubjectDisplay"].Should().Be("Jane Doe");
        result.Values["Status"].Should().Be("final");
        result.Values["ObservationId"].Should().Be("obs-1");
    }

    [Theory]
    // relative form
    [InlineData("Encounter/enc-1", "enc-1")]
    // absolute URL — the id is still the last real segment
    [InlineData("http://ehr.example.org/fhir/R4/Encounter/enc-1", "enc-1")]
    // version-specific: the id is the segment BEFORE _history, not the version number
    [InlineData("Encounter/enc-1/_history/2", "enc-1")]
    // no Type/id pair to split — the whole string is the identifier
    [InlineData("urn:uuid:2f6b6f6d-1a2b-4c3d-9e8f-7a6b5c4d3e2f", "urn:uuid:2f6b6f6d-1a2b-4c3d-9e8f-7a6b5c4d3e2f")]
    [InlineData("#contained-enc", "#contained-enc")]
    public void Every_reference_form_yields_its_id(string reference, string expected)
    {
        var json = $$$"""{"resourceType":"Observation","encounter":{"reference":"{{{reference}}}"}}""";

        var result = Sut.Map(json, [Field("EncounterId", "$.encounter.reference")]);

        result.Values["EncounterId"].Should().Be(expected);
    }

    [Fact]
    public void Array_of_references_strips_every_entry()
    {
        // Applied where values are materialized, so it covers every ArrayPolicy rather than the scalar case only.
        var json = """
            {
              "resourceType": "Observation",
              "performer": [ { "reference": "Practitioner/p-1" }, { "reference": "Practitioner/p-2" } ]
            }
            """;
        var field = new MappingFieldDto(
            "Performers", "$.performer[*].reference", MappingValueType.String,
            IsRequired: false, DefaultValue: null, Format: null, ArrayPolicy: ArrayPolicy.StoreJson);

        var result = Sut.Map(json, [field]);

        result.Values["Performers"].Should().Be("""["p-1","p-2"]""");
    }
}
