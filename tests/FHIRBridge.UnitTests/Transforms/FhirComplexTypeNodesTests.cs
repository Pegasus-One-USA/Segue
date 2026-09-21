using FHIRBridge.Application.Services.Transforms.Nodes;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Transforms;

public sealed class FhirComplexTypeNodesTests
{
    [Fact]
    public void ReferenceConstructionNode_composes_ResourceType_slash_id()
    {
        var config = new Dictionary<string, string> { ["resourceType"] = "Patient", ["display"] = "Jane Roe" };
        var result = new ReferenceConstructionNode().Execute("123", config, null);
        var reference = (System.Text.Json.Nodes.JsonObject)result.Value!;
        reference["reference"]!.GetValue<string>().Should().Be("Patient/123");
        reference["display"]!.GetValue<string>().Should().Be("Jane Roe");
    }

    [Fact]
    public void ReferenceConstructionNode_builds_a_logical_reference_via_identifier()
    {
        var config = new Dictionary<string, string> { ["style"] = "logical", ["identifierSystem"] = "http://hl7.org/fhir/sid/us-npi" };
        var result = new ReferenceConstructionNode().Execute("123", config, null);
        var reference = (System.Text.Json.Nodes.JsonObject)result.Value!;
        reference["identifier"]!["system"]!.GetValue<string>().Should().Be("http://hl7.org/fhir/sid/us-npi");
        reference["identifier"]!["value"]!.GetValue<string>().Should().Be("123");
    }

    [Fact]
    public void ReferenceConstructionNode_rejects_a_target_type_outside_the_allowed_list()
    {
        var config = new Dictionary<string, string> { ["resourceType"] = "Group", ["allowedTargetTypes"] = "Patient,RelatedPerson" };
        var result = new ReferenceConstructionNode().Execute("123", config, null);
        result.Success.Should().BeFalse();
    }

    [Fact]
    public void IdentifierFormattingNode_validates_the_NPI_check_digit()
    {
        var config = new Dictionary<string, string> { ["system"] = "http://hl7.org/fhir/sid/us-npi", ["typeCode"] = "NPI" };
        var valid = new IdentifierFormattingNode().Execute("1234567893", config, null);
        valid.Success.Should().BeTrue();

        var invalid = new IdentifierFormattingNode().Execute("1234567890", config, null);
        invalid.Success.Should().BeFalse();
    }

    [Fact]
    public void HumanNameParsingNode_parses_Last_comma_First_pattern()
    {
        var result = new HumanNameParsingNode().Execute("Roe, Jane M", new Dictionary<string, string>(), null);
        var name = (System.Text.Json.Nodes.JsonObject)result.Value!;
        name["family"]!.GetValue<string>().Should().Be("Roe");
        name["given"]!.AsArray().Select(g => g!.GetValue<string>()).Should().BeEquivalentTo(["Jane", "M"]);
    }

    [Fact]
    public void HumanNameParsingNode_extracts_a_recognized_prefix_and_suffix()
    {
        var result = new HumanNameParsingNode().Execute("Dr John Smith Jr", new Dictionary<string, string>(), null);
        var name = (System.Text.Json.Nodes.JsonObject)result.Value!;
        name["prefix"]!.AsArray()[0]!.GetValue<string>().Should().Be("Dr");
        name["suffix"]!.AsArray()[0]!.GetValue<string>().Should().Be("Jr");
        name["family"]!.GetValue<string>().Should().Be("Smith");
    }

    [Fact]
    public void HumanNameParsingNode_FirstLast_keeps_a_multi_word_surname_whole()
    {
        var config = new Dictionary<string, string> { ["pattern"] = "FirstLast" };
        var result = new HumanNameParsingNode().Execute("Warren James McGinnis III", config, null);
        var name = (System.Text.Json.Nodes.JsonObject)result.Value!;
        name["given"]!.AsArray().Select(g => g!.GetValue<string>()).Should().BeEquivalentTo(["Warren"]);
        name["family"]!.GetValue<string>().Should().Be("James McGinnis", "everything after the first token is the family name");
        name["suffix"]!.AsArray()[0]!.GetValue<string>().Should().Be("III");
        name["text"]!.GetValue<string>().Should().Be("Warren James McGinnis III");
    }

    [Fact]
    public void HumanNameParsingNode_FirstLastMiddle_takes_the_surname_from_the_second_position()
    {
        var config = new Dictionary<string, string> { ["pattern"] = "FirstLastMiddle" };
        var result = new HumanNameParsingNode().Execute("Warren James McGinnis III", config, null);
        var name = (System.Text.Json.Nodes.JsonObject)result.Value!;
        name["family"]!.GetValue<string>().Should().Be("James");
        name["given"]!.AsArray().Select(g => g!.GetValue<string>()).Should().BeEquivalentTo(["Warren", "McGinnis"]);
        name["suffix"]!.AsArray()[0]!.GetValue<string>().Should().Be("III");
    }

    [Fact]
    public void HumanNameParsingNode_strips_a_suffix_before_assigning_positions_in_every_pattern()
    {
        // Without stripping first, the trailing "Jr" would occupy the surname position in FirstLastMiddle.
        var config = new Dictionary<string, string> { ["pattern"] = "FirstLastMiddle" };
        var result = new HumanNameParsingNode().Execute("John Smith Jr", config, null);
        var name = (System.Text.Json.Nodes.JsonObject)result.Value!;
        name["family"]!.GetValue<string>().Should().Be("Smith");
        name["given"]!.AsArray().Select(g => g!.GetValue<string>()).Should().BeEquivalentTo(["John"]);
        name["suffix"]!.AsArray()[0]!.GetValue<string>().Should().Be("Jr");
    }

    [Fact]
    public void HumanNameParsingNode_treats_a_single_token_as_a_surname()
    {
        var config = new Dictionary<string, string> { ["pattern"] = "FirstLastMiddle" };
        var result = new HumanNameParsingNode().Execute("Prince", config, null);
        var name = (System.Text.Json.Nodes.JsonObject)result.Value!;
        name["family"]!.GetValue<string>().Should().Be("Prince");
        name["given"]!.AsArray().Should().BeEmpty();
    }

    [Fact]
    public void HumanNameParsingNode_formats_a_structured_name_back_into_a_display_string()
    {
        var structured = new System.Text.Json.Nodes.JsonObject
        {
            ["given"] = new System.Text.Json.Nodes.JsonArray("Warren", "McGinnis"),
            ["family"] = "James",
            ["suffix"] = new System.Text.Json.Nodes.JsonArray("III"),
        };
        var config = new Dictionary<string, string> { ["format"] = "First Middle Last Suffix" };
        var result = new HumanNameParsingNode().Execute(structured, config, null);
        result.Value.Should().Be("Warren McGinnis James III");
    }

    [Fact]
    public void HumanNameParsingNode_formats_a_structured_name_supplied_as_JSON_text()
    {
        // A structured name serialized on its way to a destination field arrives back as text, not a node.
        const string json = """{"given":["Jane"],"family":"Roe","prefix":["Dr"]}""";
        var config = new Dictionary<string, string> { ["format"] = "Prefix First Last" };
        var result = new HumanNameParsingNode().Execute(json, config, null);
        result.Value.Should().Be("Dr Jane Roe");
    }

    [Fact]
    public void HumanNameParsingNode_drops_format_parts_the_name_has_no_value_for()
    {
        var structured = new System.Text.Json.Nodes.JsonObject
        {
            ["given"] = new System.Text.Json.Nodes.JsonArray("Jane"),
            ["family"] = "Roe",
        };
        var config = new Dictionary<string, string> { ["format"] = "First Middle Last Suffix" };
        var result = new HumanNameParsingNode().Execute(structured, config, null);
        result.Value.Should().Be("Jane Roe", "the absent middle name and suffix leave no double space behind");
    }

    [Fact]
    public void HumanNameParsingNode_round_trips_a_parsed_name_back_to_the_original_string()
    {
        var parseConfig = new Dictionary<string, string> { ["pattern"] = "FirstLastMiddle" };
        var parsed = new HumanNameParsingNode().Execute("Warren James McGinnis III", parseConfig, null).Value;

        var formatConfig = new Dictionary<string, string> { ["format"] = "First Last Middle Suffix" };
        var formatted = new HumanNameParsingNode().Execute(parsed, formatConfig, null);
        formatted.Value.Should().Be("Warren James McGinnis III");
    }

    [Fact]
    public void HumanNameParsingNode_rejects_an_unrecognized_format_part()
    {
        var structured = new System.Text.Json.Nodes.JsonObject { ["family"] = "Roe" };
        var config = new Dictionary<string, string> { ["format"] = "First Nickname Last" };
        var result = new HumanNameParsingNode().Execute(structured, config, null);
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Nickname");
    }

    [Fact]
    public void HumanNameParsingNode_RoundTrip_parses_and_reformats_in_a_single_rule()
    {
        // The whole point of the RoundTrip mode: one rule on one column, fed by one string source path.
        var config = new Dictionary<string, string>
        {
            ["pattern"] = "RoundTrip",
            ["roundTripPattern"] = "FirstLastMiddle",
            ["format"] = "First Last Middle Suffix",
        };
        var result = new HumanNameParsingNode().Execute("Warren James McGinnis III", config, null);
        result.Value.Should().Be("Warren James McGinnis III", "the original string survives parse-then-format");
    }

    [Fact]
    public void HumanNameParsingNode_RoundTrip_reorders_when_the_format_differs_from_the_parse_pattern()
    {
        var config = new Dictionary<string, string>
        {
            ["pattern"] = "RoundTrip",
            ["roundTripPattern"] = "FirstLastMiddle",
            ["format"] = "First Middle Last Suffix",
        };
        var result = new HumanNameParsingNode().Execute("Warren James McGinnis III", config, null);
        result.Value.Should().Be("Warren McGinnis James III");
    }

    [Fact]
    public void HumanNameParsingNode_RoundTrip_defaults_its_parse_pattern_to_FirstLast()
    {
        var config = new Dictionary<string, string> { ["pattern"] = "RoundTrip", ["format"] = "First Last" };
        var result = new HumanNameParsingNode().Execute("Warren James McGinnis III", config, null);
        result.Value.Should().Be("Warren James McGinnis", "FirstLast keeps the multi-word surname, and III is a suffix");
    }

    [Fact]
    public void HumanNameParsingNode_RoundTrip_surfaces_a_bad_format_as_a_failure()
    {
        var config = new Dictionary<string, string> { ["pattern"] = "RoundTrip", ["format"] = "First Nickname" };
        var result = new HumanNameParsingNode().Execute("Warren James McGinnis III", config, null);
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Nickname");
    }

    [Fact]
    public void AddressParsingNode_splits_line_city_state_zip()
    {
        var result = new AddressParsingNode().Execute("123 Main St, Detroit, MI 48201", new Dictionary<string, string>(), null);
        var address = (System.Text.Json.Nodes.JsonObject)result.Value!;
        address["city"]!.GetValue<string>().Should().Be("Detroit");
        address["state"]!.GetValue<string>().Should().Be("MI");
        address["postalCode"]!.GetValue<string>().Should().Be("48201");
        address["country"]!.GetValue<string>().Should().Be("US", "the country defaults to US when not configured");
    }

    [Fact]
    public void AddressParsingNode_normalizes_a_full_state_name_to_its_abbreviation()
    {
        var result = new AddressParsingNode().Execute("123 Main St, Detroit, Michigan 48201", new Dictionary<string, string>(), null);
        var address = (System.Text.Json.Nodes.JsonObject)result.Value!;
        address["state"]!.GetValue<string>().Should().Be("MI");
    }

    [Fact]
    public void AddressParsingNode_treats_an_extra_segment_before_city_as_a_second_address_line()
    {
        var result = new AddressParsingNode().Execute("123 Main St, Apt 4B, Detroit, MI 48201", new Dictionary<string, string>(), null);
        var address = (System.Text.Json.Nodes.JsonObject)result.Value!;
        address["line"]!.AsArray().Select(l => l!.GetValue<string>()).Should().BeEquivalentTo(["123 Main St", "Apt 4B"]);
        address["city"]!.GetValue<string>().Should().Be("Detroit");
    }

    [Fact]
    public void AddressParsingNode_normalizes_a_full_country_name_to_its_ISO_3166_code()
    {
        var config = new Dictionary<string, string> { ["country"] = "Canada" };
        var result = new AddressParsingNode().Execute("1 Queen St, Toronto, ON", config, null);
        var address = (System.Text.Json.Nodes.JsonObject)result.Value!;
        address["country"]!.GetValue<string>().Should().Be("CA");
    }

    [Fact]
    public void TelecomNormalizationNode_normalizes_a_10_digit_US_number_to_E164()
    {
        var result = new TelecomNormalizationNode().Execute("(313) 555-0142", new Dictionary<string, string>(), null);
        var contact = (System.Text.Json.Nodes.JsonObject)result.Value!;
        contact["system"]!.GetValue<string>().Should().Be("phone");
        contact["value"]!.GetValue<string>().Should().Be("+13135550142");
    }

    [Fact]
    public void TelecomNormalizationNode_normalizes_a_non_US_number_using_the_configured_region()
    {
        var config = new Dictionary<string, string> { ["region"] = "GB" };
        var result = new TelecomNormalizationNode().Execute("020 7946 0958", config, null);
        var contact = (System.Text.Json.Nodes.JsonObject)result.Value!;
        contact["value"]!.GetValue<string>().Should().Be("+442079460958");
    }

    [Fact]
    public void TelecomNormalizationNode_fails_for_an_invalid_phone_number()
    {
        var result = new TelecomNormalizationNode().Execute("123", new Dictionary<string, string>(), null);
        result.Success.Should().BeFalse();
    }

    [Fact]
    public void TelecomNormalizationNode_detects_email_over_phone()
    {
        var result = new TelecomNormalizationNode().Execute("jane@example.com", new Dictionary<string, string>(), null);
        var contact = (System.Text.Json.Nodes.JsonObject)result.Value!;
        contact["system"]!.GetValue<string>().Should().Be("email");
    }

    [Fact]
    public void TelecomNormalizationNode_honors_an_explicit_fax_system_override()
    {
        var config = new Dictionary<string, string> { ["system"] = "fax", ["rank"] = "1" };
        var result = new TelecomNormalizationNode().Execute("(313) 555-0142", config, null);
        var contact = (System.Text.Json.Nodes.JsonObject)result.Value!;
        contact["system"]!.GetValue<string>().Should().Be("fax");
        contact["rank"]!.GetValue<int>().Should().Be(1);
    }
}
