using System.Text.Json.Nodes;
using FHIRBridge.Application.Services.Transforms.Nodes;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Transforms;

/// <summary>
/// One row per (reference style × config field) so it is visible at a glance which fields a style actually
/// consumes and which it silently ignores — the question the schema's visibleWhen scoping has to answer.
/// Characterization first: several cases here assert output that is WRONG but current, and say so, so that a
/// later behavioural fix has to change a test rather than slip through.
/// </summary>
public sealed class ReferenceConstructionStyleMatrixTests
{
    private static string Run(string input, params (string Key, string Value)[] config)
    {
        var result = new ReferenceConstructionNode().Execute(
            input, config.ToDictionary(c => c.Key, c => c.Value), null);

        result.Success.Should().BeTrue("this row is about the produced value, not a rejection");
        return ((JsonObject)result.Value!).ToJsonString();
    }

    // ── relative ────────────────────────────────────────────────────────────────

    [Fact]
    public void Relative_composes_type_slash_id()
    {
        Run("123", ("style", "relative"), ("resourceType", "Practitioner"))
            .Should().Be("""{"reference":"Practitioner/123"}""");
    }

    [Fact]
    public void Relative_takes_the_id_from_the_last_segment_of_a_full_url()
    {
        Run("https://ex.org/fhir/Patient/456", ("style", "relative"), ("resourceType", "Patient"))
            .Should().Be("""{"reference":"Patient/456"}""");
    }

    [Fact]
    public void Relative_ignores_baseUrl_and_identifierSystem()
    {
        Run("123",
            ("style", "relative"), ("resourceType", "Patient"),
            ("baseUrl", "https://ignored.example.com"), ("identifierSystem", "http://ignored"))
            .Should().Be("""{"reference":"Patient/123"}""");
    }

    [Fact]
    public void Relative_includes_display_when_set()
    {
        Run("123", ("style", "relative"), ("resourceType", "Patient"), ("display", "Jane Roe"))
            .Should().Be("""{"reference":"Patient/123","display":"Jane Roe"}""");
    }

    // ── absolute ────────────────────────────────────────────────────────────────

    [Fact]
    public void Absolute_prefixes_the_base_url_and_trims_its_trailing_slash()
    {
        Run("123", ("style", "absolute"), ("resourceType", "Patient"), ("baseUrl", "https://fhir.example.com/r4/"))
            .Should().Be("""{"reference":"https://fhir.example.com/r4/Patient/123"}""");
    }

    [Fact]
    public void Absolute_with_no_base_url_emits_a_malformed_reference_TODAY()
    {
        // CURRENT behaviour, not desired behaviour. config.Get("baseUrl") defaults to "", so the node
        // produces a root-relative path and reports success — an invalid reference written silently.
        Run("123", ("style", "absolute"), ("resourceType", "Patient"))
            .Should().Be("""{"reference":"/Patient/123"}""");
    }

    [Fact]
    public void Absolute_ignores_identifierSystem()
    {
        Run("123",
            ("style", "absolute"), ("resourceType", "Patient"),
            ("baseUrl", "https://fhir.example.com/r4"), ("identifierSystem", "http://ignored"))
            .Should().Be("""{"reference":"https://fhir.example.com/r4/Patient/123"}""");
    }

    // ── urn ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Urn_emits_a_uuid_urn_and_drops_the_resource_type()
    {
        Run("9e1a4f2c-0000-4000-8000-000000000001",
            ("style", "urn"), ("resourceType", "Practitioner"))
            .Should().Be("""{"reference":"urn:uuid:9e1a4f2c-0000-4000-8000-000000000001"}""");
    }

    [Fact]
    public void Urn_double_prefixes_a_value_that_is_already_a_urn_TODAY()
    {
        // CURRENT behaviour, not desired behaviour. The id is taken as "everything after the last /", and a
        // URN has no slash, so the whole value survives and the prefix is applied a second time.
        Run("urn:uuid:9e1a4f2c-0000-4000-8000-000000000001", ("style", "urn"))
            .Should().Be("""{"reference":"urn:uuid:urn:uuid:9e1a4f2c-0000-4000-8000-000000000001"}""");
    }

    [Fact]
    public void Urn_ignores_baseUrl_and_identifierSystem()
    {
        Run("9e1a4f2c-0000-4000-8000-000000000001",
            ("style", "urn"), ("baseUrl", "https://ignored.example.com"), ("identifierSystem", "http://ignored"))
            .Should().Be("""{"reference":"urn:uuid:9e1a4f2c-0000-4000-8000-000000000001"}""");
    }

    // ── logical ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Logical_emits_an_identifier_and_no_reference_at_all()
    {
        Run("123", ("style", "logical"), ("identifierSystem", "http://hl7.org/fhir/sid/us-npi"))
            .Should().Be("""{"identifier":{"system":"http://hl7.org/fhir/sid/us-npi","value":"123"}}""");
    }

    [Fact]
    public void Logical_with_no_identifier_system_emits_an_empty_system_TODAY()
    {
        // CURRENT behaviour, not desired behaviour — an identifier with no assigning authority, reported
        // as success.
        Run("123", ("style", "logical"))
            .Should().Be("""{"identifier":{"system":"","value":"123"}}""");
    }

    [Fact]
    public void Logical_ignores_baseUrl_and_resource_type_in_its_output()
    {
        Run("123",
            ("style", "logical"), ("resourceType", "Practitioner"),
            ("identifierSystem", "http://hl7.org/fhir/sid/us-npi"), ("baseUrl", "https://ignored.example.com"))
            .Should().Be("""{"identifier":{"system":"http://hl7.org/fhir/sid/us-npi","value":"123"}}""");
    }

    [Fact]
    public void Logical_includes_display_when_set()
    {
        Run("123", ("style", "logical"), ("identifierSystem", "http://x"), ("display", "Jane Roe"))
            .Should().Be("""{"identifier":{"system":"http://x","value":"123"},"display":"Jane Roe"}""");
    }

    // ── allowedTargetTypes applies to EVERY style ───────────────────────────────

    [Theory]
    [InlineData("relative")]
    [InlineData("absolute")]
    [InlineData("urn")]
    [InlineData("logical")]
    public void Allowed_target_types_are_enforced_for_every_style(string style)
    {
        // The gate runs before the style switch, so it fires even for urn and logical — whose output carries
        // no resource type at all. This is why resourceType cannot be hidden for those styles: the config
        // form prunes hidden keys on save, and the node would then fall back to its "Patient" default.
        var result = new ReferenceConstructionNode().Execute(
            "123",
            new Dictionary<string, string>
            {
                ["style"] = style,
                ["resourceType"] = "Group",
                ["allowedTargetTypes"] = "Patient,RelatedPerson",
            },
            null);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Group").And.Contain("not one of the allowed target types");
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("absolute")]
    [InlineData("urn")]
    [InlineData("logical")]
    public void An_allowed_target_type_passes_for_every_style(string style)
    {
        var result = new ReferenceConstructionNode().Execute(
            "123",
            new Dictionary<string, string>
            {
                ["style"] = style,
                ["resourceType"] = "Patient",
                ["allowedTargetTypes"] = "Patient,RelatedPerson",
                ["baseUrl"] = "https://fhir.example.com/r4",
                ["identifierSystem"] = "http://hl7.org/fhir/sid/us-npi",
            },
            null);

        result.Success.Should().BeTrue();
    }
}
