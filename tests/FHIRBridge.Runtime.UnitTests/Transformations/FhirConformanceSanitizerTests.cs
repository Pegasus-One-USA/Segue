using System.Text.Json;
using System.Text.Json.Nodes;
using FHIRBridge.Runtime.Application.Transformations;
using FluentAssertions;

namespace FHIRBridge.Runtime.UnitTests.Transformations;

public sealed class FhirConformanceSanitizerTests
{
    // ── Rule 1: Condition invariant con-5 ────────────────────────────────────────

    [Fact]
    public void Removes_clinicalStatus_when_verificationStatus_is_entered_in_error()
    {
        const string json = """
            {"resourceType":"Condition","id":"c1",
             "clinicalStatus":{"coding":[{"system":"http://terminology.hl7.org/CodeSystem/condition-clinical","code":"active"}]},
             "verificationStatus":{"coding":[{"system":"http://terminology.hl7.org/CodeSystem/condition-ver-status","code":"entered-in-error"}]}}
            """;

        var result = FhirConformanceSanitizer.Sanitize("Condition", json);

        var obj = JsonNode.Parse(result!)!.AsObject();
        obj.ContainsKey("clinicalStatus").Should().BeFalse();
        obj.ContainsKey("verificationStatus").Should().BeTrue("only clinicalStatus is stripped, not verificationStatus");
    }

    [Fact]
    public void Keeps_clinicalStatus_when_verificationStatus_is_not_entered_in_error()
    {
        const string json = """
            {"resourceType":"Condition","id":"c1",
             "clinicalStatus":{"coding":[{"code":"active"}]},
             "verificationStatus":{"coding":[{"code":"confirmed"}]}}
            """;

        var result = FhirConformanceSanitizer.Sanitize("Condition", json);

        // Nothing to fix → returns the original string reference unchanged.
        result.Should().BeSameAs(json);
    }

    [Fact]
    public void Keeps_clinicalStatus_when_there_is_no_verificationStatus()
    {
        const string json = """{"resourceType":"Condition","clinicalStatus":{"coding":[{"code":"active"}]}}""";

        FhirConformanceSanitizer.Sanitize("Condition", json).Should().BeSameAs(json);
    }

    // ── Rule 2: malformed free-text strings ──────────────────────────────────────

    [Fact]
    public void Strips_control_characters_from_string_values()
    {
        // Build valid JSON whose reasonCode.text VALUE contains a vertical-tab (U+000B). System.Text.Json emits it as
        // the legal  escape, so the JSON is well-formed — but U+000B is illegal inside a FHIR `string` (the
        // "invalid string format" eCW emits), so it must be stripped from the value.
        var reasonText = "Annual" + (char)0x0B + "visit";
        var json = new JsonObject
        {
            ["resourceType"] = "Encounter",
            ["reasonCode"] = new JsonArray(new JsonObject { ["text"] = reasonText }),
        }.ToJsonString();

        var result = FhirConformanceSanitizer.Sanitize("Encounter", json);

        JsonNode.Parse(result!)!["reasonCode"]![0]!["text"]!.GetValue<string>().Should().Be("Annualvisit");
    }

    [Fact]
    public void Trims_leading_and_trailing_whitespace_from_strings()
    {
        const string json = """{"resourceType":"Encounter","reasonCode":[{"text":"  padded  "}]}""";

        var result = FhirConformanceSanitizer.Sanitize("Encounter", json);

        JsonNode.Parse(result!)!["reasonCode"]![0]!["text"]!.GetValue<string>().Should().Be("padded");
    }

    [Fact]
    public void Preserves_tab_newline_carriage_return_inside_strings()
    {
        // \n and \t are JSON escapes here (raw string literal) → legal inside a FHIR string, and the value has no
        // leading/trailing whitespace to trim → the sanitizer makes no change and returns the same instance.
        const string json = """{"resourceType":"Observation","note":[{"text":"line1\nline2\ttabbed"}]}""";

        FhirConformanceSanitizer.Sanitize("Observation", json).Should().BeSameAs(json);
    }

    // ── General behavior ─────────────────────────────────────────────────────────

    [Fact]
    public void Leaves_a_conformant_resource_byte_for_byte_unchanged()
    {
        const string json = """{"resourceType":"Patient","id":"p1","name":[{"family":"Smith","given":["John"]}]}""";

        FhirConformanceSanitizer.Sanitize("Patient", json).Should().BeSameAs(json);
    }

    [Fact]
    public void Infers_resource_type_from_json_when_argument_is_null()
    {
        const string json = """
            {"resourceType":"Condition",
             "clinicalStatus":{"coding":[{"code":"active"}]},
             "verificationStatus":{"coding":[{"code":"entered-in-error"}]}}
            """;

        var result = FhirConformanceSanitizer.Sanitize(resourceType: null, json);

        JsonNode.Parse(result!)!.AsObject().ContainsKey("clinicalStatus").Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")] // valid JSON, but not a resource object
    public void Returns_input_untouched_for_null_blank_or_non_resource_json(string? json)
    {
        FhirConformanceSanitizer.Sanitize("Condition", json).Should().BeSameAs(json);
    }

    [Fact]
    public void Sanitized_output_is_still_valid_json()
    {
        const string json = """
            {"resourceType":"Condition",
             "clinicalStatus":{"coding":[{"code":"active"}]},
             "verificationStatus":{"coding":[{"code":"entered-in-error"}]}}
            """;

        var result = FhirConformanceSanitizer.Sanitize("Condition", json);

        Action parse = () => JsonDocument.Parse(result!);
        parse.Should().NotThrow();
    }
}
