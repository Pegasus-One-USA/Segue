using FHIRBridge.Runtime.Infrastructure.Workflows;
using FluentAssertions;

namespace FHIRBridge.Runtime.UnitTests.Workflows;

// Covers each Tier-1 "Customize fields before writing" transform (Aidbox-Customize-Transform-UX-Plan.md) plus
// the dest_fhirCustomRules parser's malformed-input handling. FhirFieldTransformApplier.Apply is the function
// MappingNodeExecutor calls per resource in customize mode — these tests exercise it directly, without a full
// executor/workflow setup.
public sealed class FhirFieldTransformApplierTests
{
    private static FhirCustomRule Rule(string field, string transform, params (string Key, string Value)[] parameters) =>
        new(field, transform, parameters.ToDictionary(p => p.Key, p => p.Value));

    [Fact]
    public void DateFormat_normalizes_a_parseable_date_to_ISO_8601_by_default()
    {
        var errors = new List<string>();
        var result = FhirFieldTransformApplier.Apply(
            """{"birthDate":"03/15/1990"}""", "Patient", [Rule("birthDate", "dateFormat")], errors);

        errors.Should().BeEmpty();
        result.Should().Contain("1990-03-15");
    }

    [Fact]
    public void DateFormat_uses_a_custom_pattern_when_targetFormat_is_custom()
    {
        var errors = new List<string>();
        var result = FhirFieldTransformApplier.Apply(
            """{"birthDate":"1990-03-15"}""", "Patient",
            [Rule("birthDate", "dateFormat", ("targetFormat", "custom"), ("pattern", "MM/dd/yyyy"))], errors);

        errors.Should().BeEmpty();
        result.Should().Contain("\"birthDate\":\"03/15/1990\"");
    }

    [Fact]
    public void DateFormat_records_an_error_and_leaves_the_value_unchanged_when_unparseable()
    {
        var errors = new List<string>();
        var sourceJson = """{"birthDate":"not-a-date"}""";
        var result = FhirFieldTransformApplier.Apply(sourceJson, "Patient", [Rule("birthDate", "dateFormat")], errors);

        result.Should().Be(sourceJson);
        errors.Should().ContainSingle(e => e.Contains("Patient.birthDate") && e.Contains("dateFormat"));
    }

    [Fact]
    public void TypeCast_converts_a_string_to_an_integer()
    {
        var errors = new List<string>();
        var result = FhirFieldTransformApplier.Apply(
            """{"count":"42"}""", "Observation", [Rule("count", "typeCast", ("targetType", "integer"))], errors);

        errors.Should().BeEmpty();
        result.Should().Contain("\"count\":42");
    }

    [Fact]
    public void TypeCast_records_an_error_for_a_non_numeric_value()
    {
        var errors = new List<string>();
        FhirFieldTransformApplier.Apply(
            """{"count":"abc"}""", "Observation", [Rule("count", "typeCast", ("targetType", "integer"))], errors);

        errors.Should().ContainSingle(e => e.Contains("Observation.count"));
    }

    [Theory]
    [InlineData("Y", true)]
    [InlineData("yes", true)]
    [InlineData("N", false)]
    [InlineData("", false)]
    public void BooleanConversion_matches_configured_true_tokens_case_insensitively(string value, bool expected)
    {
        var errors = new List<string>();
        var result = FhirFieldTransformApplier.Apply(
            $$"""{"flag":"{{value}}"}""", "Patient", [Rule("flag", "booleanConversion", ("trueValues", "Y, yes, 1"))], errors);

        errors.Should().BeEmpty();
        result.Should().Contain($"\"flag\":{expected.ToString().ToLowerInvariant()}");
    }

    [Fact]
    public void UnitConversion_converts_pounds_to_kilograms()
    {
        var errors = new List<string>();
        var result = FhirFieldTransformApplier.Apply(
            """{"weight":10}""", "Observation",
            [Rule("weight", "unitConversion", ("fromUnit", "lb"), ("toUnit", "kg"))], errors);

        errors.Should().BeEmpty();
        result.Should().Contain("\"weight\":4.5359");
    }

    [Fact]
    public void UnitConversion_converts_fahrenheit_to_celsius()
    {
        var errors = new List<string>();
        var result = FhirFieldTransformApplier.Apply(
            """{"temp":98.6}""", "Observation",
            [Rule("temp", "unitConversion", ("fromUnit", "F"), ("toUnit", "C"))], errors);

        errors.Should().BeEmpty();
        result.Should().Contain("\"temp\":37");
    }

    [Fact]
    public void UnitConversion_records_an_error_for_an_unsupported_unit_pair()
    {
        var errors = new List<string>();
        FhirFieldTransformApplier.Apply(
            """{"weight":10}""", "Observation",
            [Rule("weight", "unitConversion", ("fromUnit", "stone"), ("toUnit", "kg"))], errors);

        errors.Should().ContainSingle(e => e.Contains("unsupported unit conversion"));
    }

    [Fact]
    public void Telecom_wraps_the_field_value_into_a_ContactPoint_object()
    {
        var errors = new List<string>();
        var result = FhirFieldTransformApplier.Apply(
            """{"phone":"555-1234"}""", "Patient",
            [Rule("phone", "telecom", ("system", "phone"), ("use", "home"))], errors);

        errors.Should().BeEmpty();
        result.Should().Contain("\"phone\":{\"system\":\"phone\",\"value\":\"555-1234\",\"use\":\"home\"}");
    }

    [Fact]
    public void ReferenceConstruct_builds_a_Reference_object_from_a_raw_id_value()
    {
        var errors = new List<string>();
        var result = FhirFieldTransformApplier.Apply(
            """{"managingOrganization":"org-123"}""", "Patient",
            [Rule("managingOrganization", "referenceConstruct", ("targetResourceType", "Organization"))], errors);

        errors.Should().BeEmpty();
        result.Should().Contain("\"managingOrganization\":{\"reference\":\"Organization/org-123\"}");
    }

    [Fact]
    public void Coalesce_sets_the_default_only_when_the_field_is_missing_or_empty()
    {
        var errors = new List<string>();
        var result = FhirFieldTransformApplier.Apply(
            """{"class":""}""", "Encounter", [Rule("class", "coalesce", ("defaultValue", "AMB"))], errors);

        errors.Should().BeEmpty();
        result.Should().Contain("\"class\":\"AMB\"");
    }

    [Fact]
    public void Coalesce_does_not_overwrite_an_existing_value()
    {
        var errors = new List<string>();
        var result = FhirFieldTransformApplier.Apply(
            """{"class":"IMP"}""", "Encounter", [Rule("class", "coalesce", ("defaultValue", "AMB"))], errors);

        errors.Should().BeEmpty();
        result.Should().Contain("\"class\":\"IMP\"");
    }

    [Fact]
    public void Nested_and_indexed_field_paths_are_resolved()
    {
        var errors = new List<string>();
        var result = FhirFieldTransformApplier.Apply(
            """{"identifier":[{"system":"local"},{"system":"other"}]}""", "Patient",
            [Rule("identifier[1].system", "typeCast", ("targetType", "string"))], errors);

        errors.Should().BeEmpty();
        result.Should().Contain("\"system\":\"other\"");
    }

    [Fact]
    public void A_field_path_that_does_not_exist_on_the_resource_is_recorded_as_an_error_not_thrown()
    {
        var errors = new List<string>();
        var sourceJson = """{"resourceType":"Patient"}""";
        var result = FhirFieldTransformApplier.Apply(
            sourceJson, "Patient", [Rule("nonexistent.path", "dateFormat")], errors);

        result.Should().Be(sourceJson);
        errors.Should().ContainSingle();
    }

    [Fact]
    public void Empty_rule_list_returns_the_source_json_unchanged()
    {
        var sourceJson = """{"resourceType":"Patient","id":"1"}""";
        var errors = new List<string>();
        FhirFieldTransformApplier.Apply(sourceJson, "Patient", [], errors).Should().Be(sourceJson);
    }

    // ── the remaining 13 transforms (Aidbox-Customize-Transform-UX-Plan.md's "All 20" section) ──────────────

    [Fact]
    public void QuantityRange_builds_a_single_quantity_with_unit_and_comparator()
    {
        var errors = new List<string>();
        var result = FhirFieldTransformApplier.Apply(
            """{"value":"<5"}""", "Observation",
            [Rule("value", "quantityRange", ("mode", "quantity"), ("unit", "mg"))], errors);

        errors.Should().BeEmpty();
        result.Should().Contain("\"comparator\":\"<\"").And.Contain("\"value\":5").And.Contain("\"unit\":\"mg\"");
    }

    [Fact]
    public void QuantityRange_splits_a_low_high_range()
    {
        var errors = new List<string>();
        var result = FhirFieldTransformApplier.Apply(
            """{"value":"10-20"}""", "Observation",
            [Rule("value", "quantityRange", ("mode", "range"), ("unit", "mg"), ("delimiter", "-"))], errors);

        errors.Should().BeEmpty();
        result.Should().Contain("\"low\":{\"value\":10").And.Contain("\"high\":{\"value\":20");
    }

    [Fact]
    public void RoundPrecision_rounds_to_the_configured_decimal_places()
    {
        var errors = new List<string>();
        var result = FhirFieldTransformApplier.Apply(
            """{"value":3.14159}""", "Observation", [Rule("value", "roundPrecision", ("decimals", "2"))], errors);

        errors.Should().BeEmpty();
        result.Should().Contain("\"value\":3.14");
    }

    [Fact]
    public void CodeLookup_maps_a_source_value_via_configured_pairs()
    {
        var pairsJson = """[{"from":"M","to":"male"},{"from":"F","to":"female"}]""";
        var errors = new List<string>();
        var result = FhirFieldTransformApplier.Apply(
            """{"gender":"M"}""", "Patient", [Rule("gender", "codeLookup", ("pairs", pairsJson))], errors);

        errors.Should().BeEmpty();
        result.Should().Contain("\"gender\":\"male\"");
    }

    [Fact]
    public void CodeLookup_records_an_error_when_no_pair_matches()
    {
        var errors = new List<string>();
        FhirFieldTransformApplier.Apply(
            """{"gender":"X"}""", "Patient",
            [Rule("gender", "codeLookup", ("pairs", """[{"from":"M","to":"male"}]"""))], errors);

        errors.Should().ContainSingle(e => e.Contains("Patient.gender"));
    }

    [Fact]
    public void StatusCoercion_uses_the_same_lookup_mechanism_as_codeLookup()
    {
        var errors = new List<string>();
        var result = FhirFieldTransformApplier.Apply(
            """{"status":"done"}""", "Observation",
            [Rule("status", "statusCoercion", ("pairs", """[{"from":"done","to":"final"}]"""))], errors);

        errors.Should().BeEmpty();
        result.Should().Contain("\"status\":\"final\"");
    }

    [Fact]
    public void CodeableConcept_wraps_the_field_value_as_a_coding_entry()
    {
        var errors = new List<string>();
        var result = FhirFieldTransformApplier.Apply(
            """{"code":"38341003"}""", "Condition",
            [Rule("code", "codeableConcept", ("system", "http://snomed.info/sct"), ("display", "Hypertension"))], errors);

        errors.Should().BeEmpty();
        result.Should().Contain("\"coding\":[{\"system\":\"http://snomed.info/sct\",\"code\":\"38341003\",\"display\":\"Hypertension\"}]");
    }

    [Fact]
    public void IdentifierFormat_wraps_the_value_into_a_system_value_pair()
    {
        var errors = new List<string>();
        var result = FhirFieldTransformApplier.Apply(
            """{"mrn":"12345"}""", "Patient",
            [Rule("mrn", "identifierFormat", ("system", "http://hospital.org/mrn"))], errors);

        errors.Should().BeEmpty();
        result.Should().Contain("\"mrn\":{\"system\":\"http://hospital.org/mrn\",\"value\":\"12345\"}");
    }

    [Fact]
    public void HumanNameFormat_splits_family_and_given_on_the_configured_delimiter()
    {
        var errors = new List<string>();
        var result = FhirFieldTransformApplier.Apply(
            """{"fullName":"Smith, John"}""", "Patient",
            [Rule("fullName", "humanNameFormat", ("delimiter", ", "))], errors);

        errors.Should().BeEmpty();
        result.Should().Contain("\"family\":\"Smith\"").And.Contain("\"given\":[\"John\"]");
    }

    [Fact]
    public void AddressParse_splits_a_flat_string_into_line_city_state_postalCode()
    {
        var errors = new List<string>();
        var result = FhirFieldTransformApplier.Apply(
            """{"addr":"123 Main St, Springfield, IL, 62704"}""", "Patient",
            [Rule("addr", "addressParse", ("delimiter", ","))], errors);

        errors.Should().BeEmpty();
        result.Should().Contain("\"city\":\"Springfield\"").And.Contain("\"state\":\"IL\"").And.Contain("\"postalCode\":\"62704\"");
    }

    [Theory]
    [InlineData("upper", "HELLO")]
    [InlineData("lower", "hello")]
    [InlineData("trim", "Hello")]
    public void StringClean_applies_the_configured_mode(string mode, string expected)
    {
        var errors = new List<string>();
        var result = FhirFieldTransformApplier.Apply(
            """{"value":"  Hello  "}""", "Patient", [Rule("value", "stringClean", ("mode", mode))], errors);

        errors.Should().BeEmpty();
        result.Should().Contain($"\"value\":\"{expected}\"");
    }

    [Fact]
    public void StringTemplate_wraps_the_value_using_the_template_placeholder()
    {
        var errors = new List<string>();
        var result = FhirFieldTransformApplier.Apply(
            """{"mrn":"12345"}""", "Patient",
            [Rule("mrn", "stringTemplate", ("mode", "template"), ("template", "MRN-{value}"))], errors);

        errors.Should().BeEmpty();
        result.Should().Contain("\"mrn\":\"MRN-12345\"");
    }

    [Fact]
    public void StringTemplate_splits_into_an_array_in_split_mode()
    {
        var errors = new List<string>();
        var result = FhirFieldTransformApplier.Apply(
            """{"tags":"a,b,c"}""", "Patient",
            [Rule("tags", "stringTemplate", ("mode", "split"), ("delimiter", ","))], errors);

        errors.Should().BeEmpty();
        result.Should().Contain("\"tags\":[\"a\",\"b\",\"c\"]");
    }

    [Fact]
    public void ArrayOp_first_replaces_the_array_with_its_first_element()
    {
        var errors = new List<string>();
        var result = FhirFieldTransformApplier.Apply(
            """{"name":[{"family":"Smith"},{"family":"Jones"}]}""", "Patient",
            [Rule("name", "arrayOp", ("operation", "first"))], errors);

        errors.Should().BeEmpty();
        result.Should().Contain("\"name\":{\"family\":\"Smith\"}");
    }

    [Fact]
    public void ArrayOp_filterBySystem_finds_the_matching_entry()
    {
        var errors = new List<string>();
        var result = FhirFieldTransformApplier.Apply(
            """{"identifier":[{"system":"local","value":"1"},{"system":"mrn","value":"2"}]}""", "Patient",
            [Rule("identifier", "arrayOp", ("operation", "filterBySystem"), ("filterSystem", "mrn"))], errors);

        errors.Should().BeEmpty();
        result.Should().Contain("\"identifier\":{\"system\":\"mrn\",\"value\":\"2\"}");
    }

    [Fact]
    public void ArrayOp_join_concatenates_string_array_elements()
    {
        var errors = new List<string>();
        var result = FhirFieldTransformApplier.Apply(
            """{"given":["John","Michael"]}""", "Patient",
            [Rule("given", "arrayOp", ("operation", "join"), ("joinDelimiter", " "))], errors);

        errors.Should().BeEmpty();
        result.Should().Contain("\"given\":\"John Michael\"");
    }

    [Fact]
    public void ArrayOp_records_an_error_when_the_field_is_not_an_array()
    {
        var errors = new List<string>();
        FhirFieldTransformApplier.Apply(
            """{"name":"not-an-array"}""", "Patient", [Rule("name", "arrayOp", ("operation", "first"))], errors);

        errors.Should().ContainSingle(e => e.Contains("Patient.name"));
    }

    [Fact]
    public void DateShift_shifts_a_date_by_the_configured_number_of_days()
    {
        var errors = new List<string>();
        var result = FhirFieldTransformApplier.Apply(
            """{"birthDate":"2000-01-10"}""", "Patient", [Rule("birthDate", "dateShift", ("days", "-5"))], errors);

        errors.Should().BeEmpty();
        result.Should().Contain("2000-01-05");
    }

    [Fact]
    public void HashMask_hash_mode_produces_a_SHA256_hex_digest()
    {
        var errors = new List<string>();
        var result = FhirFieldTransformApplier.Apply(
            """{"ssn":"123-45-6789"}""", "Patient", [Rule("ssn", "hashMask", ("mode", "hash"))], errors);

        errors.Should().BeEmpty();
        result.Should().MatchRegex("\"ssn\":\"[0-9A-F]{64}\"");
    }

    [Fact]
    public void HashMask_mask_mode_keeps_only_the_last_N_characters()
    {
        var errors = new List<string>();
        var result = FhirFieldTransformApplier.Apply(
            """{"ssn":"123-45-6789"}""", "Patient", [Rule("ssn", "hashMask", ("mode", "mask"), ("showLastN", "4"))], errors);

        errors.Should().BeEmpty();
        result.Should().Contain("\"ssn\":\"*******6789\"");
    }
}

public sealed class FhirCustomRulesParserTests
{
    [Fact]
    public void Parses_rules_grouped_by_resource_type()
    {
        var json = """
        {
            "Patient": [{"field":"birthDate","transform":"dateFormat","params":{"targetFormat":"iso8601"}}],
            "Observation": [{"field":"status","transform":"typeCast","params":{"targetType":"string"}}]
        }
        """;

        var parsed = FhirCustomRulesParser.Parse(json);

        parsed.Should().ContainKey("Patient");
        parsed["Patient"].Should().ContainSingle(r => r.Field == "birthDate" && r.Transform == "dateFormat");
        parsed["Observation"].Should().ContainSingle(r => r.Field == "status");
    }

    [Fact]
    public void Malformed_json_returns_an_empty_map_rather_than_throwing()
    {
        FhirCustomRulesParser.Parse("not json").Should().BeEmpty();
    }

    [Fact]
    public void Null_or_empty_input_returns_an_empty_map()
    {
        FhirCustomRulesParser.Parse(null).Should().BeEmpty();
        FhirCustomRulesParser.Parse("").Should().BeEmpty();
    }

    [Fact]
    public void A_rule_missing_field_or_transform_is_skipped_not_thrown()
    {
        var json = """{"Patient": [{"field":"","transform":"dateFormat"}, {"field":"birthDate","transform":""}]}""";
        FhirCustomRulesParser.Parse(json)["Patient"].Should().BeEmpty();
    }
}
