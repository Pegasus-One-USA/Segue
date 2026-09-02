using FHIRBridge.Application.Abstractions.Terminology;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services.Transforms.Nodes;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Transforms;

public sealed class CodesTerminologyNodesTests
{
    private sealed class FakeTerminologyLookupService : ITerminologyLookupService
    {
        private readonly TerminologyLookupResult? _result;
        private readonly TerminologyLookupResult? _anySystemResult;

        public FakeTerminologyLookupService(TerminologyLookupResult? result, TerminologyLookupResult? anySystemResult = null)
        {
            _result = result;
            _anySystemResult = anySystemResult;
        }

        public Task<TerminologyLookupResult?> LookupAsync(string system, string code, CancellationToken cancellationToken) =>
            Task.FromResult(_result);

        public Task<TerminologyLookupResult?> LookupAnyLocalSystemAsync(string code, CancellationToken cancellationToken) =>
            Task.FromResult(_anySystemResult);
    }

    [Fact]
    public void ValueCodeMappingNode_translates_via_the_configured_map()
    {
        var config = new Dictionary<string, string> { ["map"] = """{"M":"male","F":"female"}""" };
        new ValueCodeMappingNode().Execute("M", config, null).Value.Should().Be("male");
    }

    [Fact]
    public void ValueCodeMappingNode_returns_null_for_unmatched_by_default()
    {
        var config = new Dictionary<string, string> { ["map"] = """{"M":"male"}""" };
        new ValueCodeMappingNode().Execute("X", config, null).Value.Should().BeNull();
    }

    [Fact]
    public void ValueCodeMappingNode_emits_a_full_coding_when_configured()
    {
        var config = new Dictionary<string, string>
        {
            ["map"] = """{"M":"male"}""",
            ["emitCoding"] = "true",
            ["codingSystem"] = "http://hl7.org/fhir/administrative-gender",
        };
        var result = new ValueCodeMappingNode().Execute("M", config, null);
        var coding = (System.Text.Json.Nodes.JsonObject)result.Value!;
        coding["code"]!.GetValue<string>().Should().Be("male");
        coding["system"]!.GetValue<string>().Should().Be("http://hl7.org/fhir/administrative-gender");
    }

    [Fact]
    public void CodeableConceptBuilderNode_resolves_a_short_system_key_to_its_canonical_uri()
    {
        var config = new Dictionary<string, string> { ["system"] = "LOINC", ["display"] = "Glucose" };
        var result = new CodeableConceptBuilderNode().Execute("2339-0", config, null);
        var concept = (System.Text.Json.Nodes.JsonObject)result.Value!;
        var coding = (System.Text.Json.Nodes.JsonObject)concept["coding"]![0]!;
        coding["system"]!.GetValue<string>().Should().Be("http://loinc.org");
        coding["code"]!.GetValue<string>().Should().Be("2339-0");
        concept["text"]!.GetValue<string>().Should().Be("Glucose");
    }

    [Fact]
    public void CodeableConceptBuilderNode_supports_a_second_coding_alongside_the_primary_one()
    {
        var config = new Dictionary<string, string>
        {
            ["system"] = "LOINC",
            ["additionalCodings"] = """[{"system":"SNOMED","code":"1234","display":"Something"}]""",
        };
        var result = new CodeableConceptBuilderNode().Execute("2339-0", config, null);
        var concept = (System.Text.Json.Nodes.JsonObject)result.Value!;
        var codings = concept["coding"]!.AsArray();
        codings.Should().HaveCount(2);
        ((System.Text.Json.Nodes.JsonObject)codings[1]!)["system"]!.GetValue<string>().Should().Be("http://snomed.info/sct");
    }

    [Fact]
    public async Task CodeableConceptBuilderNode_resolves_display_from_terminology_when_config_display_is_blank()
    {
        var lookup = new FakeTerminologyLookupService(new TerminologyLookupResult(
            "http://hl7.org/fhir/sid/icd-10-cm", "E11.9", "Type 2 diabetes mellitus without complications", "2026", "Icd10Database"));
        var config = new Dictionary<string, string> { ["system"] = "ICD10" };
        var result = await new CodeableConceptBuilderNode(lookup).ExecuteAsync("E11.9", config, null);
        var concept = (System.Text.Json.Nodes.JsonObject)result.Value!;
        var coding = (System.Text.Json.Nodes.JsonObject)concept["coding"]![0]!;
        coding["display"]!.GetValue<string>().Should().Be("Type 2 diabetes mellitus without complications");
        concept["text"]!.GetValue<string>().Should().Be("Type 2 diabetes mellitus without complications");
    }

    [Fact]
    public async Task CodeableConceptBuilderNode_falls_back_to_bare_code_when_terminology_lookup_misses()
    {
        var lookup = new FakeTerminologyLookupService(null);
        var config = new Dictionary<string, string> { ["system"] = "ICD10" };
        var result = await new CodeableConceptBuilderNode(lookup).ExecuteAsync("Z99.9", config, null);
        var concept = (System.Text.Json.Nodes.JsonObject)result.Value!;
        concept["text"]!.GetValue<string>().Should().Be("Z99.9");
    }

    [Fact]
    public async Task CodeableConceptBuilderNode_never_overrides_an_explicit_display()
    {
        var lookup = new FakeTerminologyLookupService(new TerminologyLookupResult(
            "http://hl7.org/fhir/sid/icd-10-cm", "E11.9", "Should not be used", "2026", "Icd10Database"));
        var config = new Dictionary<string, string> { ["system"] = "ICD10", ["display"] = "My Own Label" };
        var result = await new CodeableConceptBuilderNode(lookup).ExecuteAsync("E11.9", config, null);
        var concept = (System.Text.Json.Nodes.JsonObject)result.Value!;
        concept["text"]!.GetValue<string>().Should().Be("My Own Label");
    }

    [Fact]
    public async Task CodeableConceptBuilderNode_falls_back_to_the_source_json_display_hint_before_the_bare_code()
    {
        var lookup = new FakeTerminologyLookupService(null);
        var config = new Dictionary<string, string>
        {
            ["system"] = "ICD10",
            [FHIRBridge.Application.Services.Transforms.ReservedTransformConfigKeys.SourceDisplayHint] = "Lobar pneumonia, unspecified organism",
        };
        var result = await new CodeableConceptBuilderNode(lookup).ExecuteAsync("J18.1", config, null);
        var concept = (System.Text.Json.Nodes.JsonObject)result.Value!;
        concept["text"]!.GetValue<string>().Should().Be("Lobar pneumonia, unspecified organism");
    }

    [Fact]
    public async Task CodeableConceptBuilderNode_prefers_terminology_db_over_the_source_json_display_hint()
    {
        var lookup = new FakeTerminologyLookupService(new TerminologyLookupResult(
            "http://hl7.org/fhir/sid/icd-10-cm", "J18.1", "Real DB display", "2026", "Icd10Database"));
        var config = new Dictionary<string, string>
        {
            ["system"] = "ICD10",
            [FHIRBridge.Application.Services.Transforms.ReservedTransformConfigKeys.SourceDisplayHint] = "Should not be used",
        };
        var result = await new CodeableConceptBuilderNode(lookup).ExecuteAsync("J18.1", config, null);
        var concept = (System.Text.Json.Nodes.JsonObject)result.Value!;
        concept["text"]!.GetValue<string>().Should().Be("Real DB display");
    }

    [Fact]
    public async Task CodeableConceptBuilderNode_skips_lookup_when_explicitly_disabled()
    {
        var lookup = new FakeTerminologyLookupService(new TerminologyLookupResult(
            "http://hl7.org/fhir/sid/icd-10-cm", "E11.9", "Should not be used", "2026", "Icd10Database"));
        var config = new Dictionary<string, string> { ["system"] = "ICD10", ["resolveDisplayFromTerminology"] = "false" };
        var result = await new CodeableConceptBuilderNode(lookup).ExecuteAsync("E11.9", config, null);
        var concept = (System.Text.Json.Nodes.JsonObject)result.Value!;
        concept["text"]!.GetValue<string>().Should().Be("E11.9");
    }

    [Fact]
    public async Task CodeableConceptBuilderNode_ignores_a_different_local_system_match_when_autoDetect_is_off_by_default()
    {
        // A wildcard source field can extract codes from multiple codings using different systems, but
        // without opting in, a rule configured for ICD10 must never silently borrow a SNOMED match — that
        // would be a behavior change nobody asked for. The configured-system miss should just fall through
        // to the bare code, same as before this feature existed.
        var lookup = new FakeTerminologyLookupService(
            result: null,
            anySystemResult: new TerminologyLookupResult("http://snomed.info/sct", "243876005", "Screening status", "20260901", "HapiLocalTerminologyDatabase"));
        var config = new Dictionary<string, string> { ["system"] = "ICD10" };
        var result = await new CodeableConceptBuilderNode(lookup).ExecuteAsync("243876005", config, null);
        var concept = (System.Text.Json.Nodes.JsonObject)result.Value!;
        var coding = (System.Text.Json.Nodes.JsonObject)concept["coding"]![0]!;
        coding["system"]!.GetValue<string>().Should().Be("http://hl7.org/fhir/sid/icd-10-cm");
        concept["text"]!.GetValue<string>().Should().Be("243876005");
        result.ResolvedSystemOverride.Should().BeNull();
    }

    [Fact]
    public async Task CodeableConceptBuilderNode_corrects_the_system_when_autoDetect_finds_the_code_under_a_different_local_system()
    {
        var lookup = new FakeTerminologyLookupService(
            result: null,
            anySystemResult: new TerminologyLookupResult("http://snomed.info/sct", "243876005", "Screening status", "20260901", "HapiLocalTerminologyDatabase"));
        var config = new Dictionary<string, string> { ["system"] = "ICD10", ["autoDetectSystemOnLocalMiss"] = "true" };
        var result = await new CodeableConceptBuilderNode(lookup).ExecuteAsync("243876005", config, null);
        var concept = (System.Text.Json.Nodes.JsonObject)result.Value!;
        var coding = (System.Text.Json.Nodes.JsonObject)concept["coding"]![0]!;

        // The emitted coding must be internally consistent — a SNOMED-only code paired with the ICD-10-CM
        // system URI would be invalid FHIR, so the system itself is corrected, not just the display text.
        coding["system"]!.GetValue<string>().Should().Be("http://snomed.info/sct");
        coding["display"]!.GetValue<string>().Should().Be("Screening status");
        result.ResolvedSystemOverride.Should().Be("http://snomed.info/sct");
    }

    [Fact]
    public async Task CodeableConceptBuilderNode_autoDetect_does_not_flag_an_override_when_the_code_matches_the_configured_system()
    {
        var lookup = new FakeTerminologyLookupService(
            result: null,
            anySystemResult: new TerminologyLookupResult("http://hl7.org/fhir/sid/icd-10-cm", "E11.9", "Type 2 diabetes mellitus without complications", "2026", "HapiLocalTerminologyDatabase"));
        var config = new Dictionary<string, string> { ["system"] = "ICD10", ["autoDetectSystemOnLocalMiss"] = "true" };
        var result = await new CodeableConceptBuilderNode(lookup).ExecuteAsync("E11.9", config, null);
        var concept = (System.Text.Json.Nodes.JsonObject)result.Value!;
        var coding = (System.Text.Json.Nodes.JsonObject)concept["coding"]![0]!;

        coding["system"]!.GetValue<string>().Should().Be("http://hl7.org/fhir/sid/icd-10-cm");
        result.ResolvedSystemOverride.Should().BeNull();
    }

    [Fact]
    public async Task CodeableConceptBuilderNode_autoDetect_falls_back_to_the_bare_code_when_no_local_system_has_it_either()
    {
        var lookup = new FakeTerminologyLookupService(result: null, anySystemResult: null);
        var config = new Dictionary<string, string> { ["system"] = "ICD10", ["autoDetectSystemOnLocalMiss"] = "true" };
        var result = await new CodeableConceptBuilderNode(lookup).ExecuteAsync("Z99.9", config, null);
        var concept = (System.Text.Json.Nodes.JsonObject)result.Value!;
        concept["text"]!.GetValue<string>().Should().Be("Z99.9");
        result.ResolvedSystemOverride.Should().BeNull();
    }

    [Fact]
    public void CodeableConceptBuilderNode_emits_display_text_only_when_configured()
    {
        var config = new Dictionary<string, string>
        {
            ["system"] = "ICD10",
            ["display"] = "Lobar pneumonia, unspecified organism",
            ["outputShape"] = "displayTextOnly",
        };
        var result = new CodeableConceptBuilderNode().Execute("J18.1", config, null);
        result.Value.Should().Be("Lobar pneumonia, unspecified organism");
    }

    [Fact]
    public void CodeableConceptBuilderNode_display_text_only_falls_back_to_bare_code_with_no_display()
    {
        var config = new Dictionary<string, string> { ["system"] = "ICD10", ["outputShape"] = "displayTextOnly" };
        var result = new CodeableConceptBuilderNode().Execute("J18.1", config, null);
        result.Value.Should().Be("J18.1");
    }

    [Fact]
    public void CodeableConceptBuilderNode_defaults_to_the_full_object_when_outputShape_is_unset()
    {
        var config = new Dictionary<string, string> { ["system"] = "ICD10", ["display"] = "Test" };
        var result = new CodeableConceptBuilderNode().Execute("J18.1", config, null);
        result.Value.Should().BeOfType<System.Text.Json.Nodes.JsonObject>();
    }

    [Fact]
    public void StatusEnumCoercionNode_never_emits_an_out_of_value_set_code()
    {
        var config = new Dictionary<string, string>
        {
            ["map"] = """{"COMPLETE":"final"}""",
            ["fallback"] = "unknown"
        };
        new StatusEnumCoercionNode().Execute("weird-status", config, null).Value.Should().Be("unknown");
        new StatusEnumCoercionNode().Execute("COMPLETE", config, null).Value.Should().Be("final");
    }
}
