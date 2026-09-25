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

    /// <summary>
    /// An element states its own parts, so the mode selects the order they are COMPOSED in — nothing is
    /// parsed out of the display string. See HumanNameArrayModeSpecTests for the full table of modes.
    /// </summary>
    [Fact]
    public void HumanNameParsingNode_composes_an_element_in_the_order_its_mode_names()
    {
        const string nameJson =
            """{"use":"official","text":"Warren James McGinnis III","family":"McGinnis","given":["Warren","James"]}""";

        var config = new Dictionary<string, string> { ["pattern"] = "FirstMiddleLast" };
        new HumanNameParsingNode().Execute(nameJson, config, null).Value.Should().Be("Warren James McGinnis");
    }

    /// <summary>An explicit output format still wins over the mode's own order — this is the direction every
    /// chained rule that feeds a structured name into this node relies on.</summary>
    [Fact]
    public void HumanNameParsingNode_still_formats_a_structured_name_when_a_format_is_configured()
    {
        const string nameJson =
            """{"use":"official","text":"Warren James McGinnis III","family":"McGinnis","given":["Warren","James"]}""";

        var config = new Dictionary<string, string> { ["format"] = "Last First Middle" };
        new HumanNameParsingNode().Execute(nameJson, config, null).Value.Should().Be("McGinnis Warren James");
    }

    /// <summary>An element with no display string composes from its parts just the same — there is simply no
    /// affix to recover.</summary>
    [Fact]
    public void HumanNameParsingNode_composes_an_element_that_carries_no_text()
    {
        const string nameJson = """{"family":"Roe","given":["Jane"]}""";

        new HumanNameParsingNode().Execute(nameJson, new Dictionary<string, string>(), null)
            .Value.Should().Be("Jane Roe");
    }

    /// <summary>
    /// The reported case: the whole name element in, "Warren McGinnis III" out. The element's own family/given
    /// are used, the suffix is recovered from `text` (FHIR leaves it nowhere else here), and the output names
    /// First + Last + Suffix — so the middle name is dropped, which is the point of choosing that shape.
    /// </summary>
    [Fact]
    public void HumanNameParsingNode_composes_the_named_output_shape_from_a_whole_name_element()
    {
        const string nameJson =
            """{"use":"official","text":"Warren James McGinnis III","family":"McGinnis","given":["Warren","James"]}""";

        var result = new HumanNameParsingNode().Execute(
            nameJson,
            new Dictionary<string, string> { ["pattern"] = "FirstMiddleLast", ["format"] = "First Last Suffix" },
            null);

        result.Value.Should().Be("Warren McGinnis III");
    }

    /// <summary>
    /// The same output shape fed the bare display string instead of the element. There are no parts to trust,
    /// so `pattern` decides where the surname starts — and FirstLast deliberately keeps a multi-word surname
    /// whole, which is why this reads "James McGinnis" where the element-fed case above reads "McGinnis".
    /// Pinned because it is the one case where the two sources legitimately disagree.
    /// </summary>
    [Fact]
    public void HumanNameParsingNode_composing_from_a_bare_string_depends_on_the_input_pattern()
    {
        var config = new Dictionary<string, string> { ["format"] = "First Last Suffix" };

        config["pattern"] = "FirstLast";
        new HumanNameParsingNode().Execute("Warren James McGinnis III", config, null)
            .Value.Should().Be("Warren James McGinnis III");

        config["pattern"] = "FirstMiddleLast";
        new HumanNameParsingNode().Execute("Warren James McGinnis III", config, null)
            .Value.Should().Be("Warren McGinnis III");
    }

    /// <summary>
    /// A format that does not name Suffix drops it — the affix is still parsed off the string (so it cannot
    /// be mistaken for part of the surname), it simply is not emitted. Pinned alongside the pattern pair above
    /// because the two settings are routinely confused for one another: this differs from those only in what
    /// it asks for, not in how the string was read.
    /// </summary>
    [Fact]
    public void HumanNameParsingNode_omits_a_part_the_output_format_does_not_name()
    {
        var config = new Dictionary<string, string> { ["format"] = "First Last" };

        config["pattern"] = "FirstLast";
        new HumanNameParsingNode().Execute("Warren James McGinnis III", config, null)
            .Value.Should().Be("Warren James McGinnis", "FirstLast reads every word after the first as the surname");

        config["pattern"] = "FirstMiddleLast";
        new HumanNameParsingNode().Execute("Warren James McGinnis III", config, null)
            .Value.Should().Be("Warren McGinnis", "FirstMiddleLast reads only the last word as the surname");
    }

    /// <summary>The plain Western reading: the last token is the surname, everything before it is a given name.
    /// FirstLast cannot express this — it treats everything after the first token as one multi-word
    /// surname — which is why a middle name had no pattern that handled it.</summary>
    [Fact]
    public void HumanNameParsingNode_FirstMiddleLast_takes_the_surname_from_the_last_position()
    {
        var config = new Dictionary<string, string> { ["pattern"] = "FirstMiddleLast" };

        var result = new HumanNameParsingNode().Execute("Warren James McGinnis III", config, null);

        var name = (System.Text.Json.Nodes.JsonObject)result.Value!;
        name["family"]!.GetValue<string>().Should().Be("McGinnis");
        name["given"]!.AsArray().Select(g => g!.GetValue<string>()).Should().Equal("Warren", "James");
        name["suffix"]!.AsArray().Select(x => x!.GetValue<string>()).Should().Equal("III");
    }

    /// <summary>
    /// LastFirstMiddle is comma-driven: it splits on "," and assigns everything before it to family. Given a
    /// string with NO comma that is the whole string, and the prefix/suffix stripping in the positional branch
    /// never runs either — so a First-Middle-Last source parsed under this pattern yields one giant family name
    /// and no given names. Pinned because it is what a whole-node rule left on the default-looking
    /// LastFirstMiddle actually produces, and it reads as "the transform is broken" rather than "the pattern
    /// does not match this data".
    /// </summary>
    [Fact]
    public void HumanNameParsingNode_LastFirstMiddle_without_a_comma_puts_the_whole_string_in_family()
    {
        var config = new Dictionary<string, string> { ["pattern"] = "LastFirstMiddle" };

        var result = new HumanNameParsingNode().Execute("Warren James McGinnis III", config, null);

        var name = (System.Text.Json.Nodes.JsonObject)result.Value!;
        name["family"]!.GetValue<string>().Should().Be("Warren James McGinnis III");
        name["given"]!.AsArray().Should().BeEmpty();
        name.ContainsKey("suffix").Should().BeFalse("the comma branch never reaches the affix stripping");
    }

    /// <summary>A lone token is a surname under FirstMiddleLast too, the same fallback every other positional
    /// pattern drops to.</summary>
    [Fact]
    public void HumanNameParsingNode_FirstMiddleLast_treats_a_single_token_as_a_surname()
    {
        var config = new Dictionary<string, string> { ["pattern"] = "FirstMiddleLast" };

        var result = new HumanNameParsingNode().Execute("Prince", config, null);

        var name = (System.Text.Json.Nodes.JsonObject)result.Value!;
        name["family"]!.GetValue<string>().Should().Be("Prince");
        name["given"]!.AsArray().Should().BeEmpty();
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

    // The dropdown only ever emits lowercase, but an imported mapping profile or an API-set config can carry
    // "FAX" or " fax ". Those used to miss the override branch silently and fall through to phone parsing.
    [Theory]
    [InlineData("FAX")]
    [InlineData("Fax")]
    [InlineData(" fax ")]
    public void TelecomNormalizationNode_matches_the_system_override_regardless_of_case_or_padding(string configured)
    {
        var config = new Dictionary<string, string> { ["system"] = configured };
        var result = new TelecomNormalizationNode().Execute("(313) 555-0142", config, null);
        var contact = (System.Text.Json.Nodes.JsonObject)result.Value!;
        contact["system"]!.GetValue<string>().Should().Be("fax");
        contact["value"]!.GetValue<string>().Should().Be("(313) 555-0142");
    }

    [Theory]
    [InlineData("sms")]
    [InlineData("pager")]
    [InlineData("other")]
    public void TelecomNormalizationNode_supports_the_remaining_contact_point_systems(string configured)
    {
        var config = new Dictionary<string, string> { ["system"] = configured };
        var result = new TelecomNormalizationNode().Execute("(313) 555-0142", config, null);
        ((System.Text.Json.Nodes.JsonObject)result.Value!)["system"]!.GetValue<string>().Should().Be(configured);
    }

    [Fact]
    public void TelecomNormalizationNode_pins_the_phone_branch_when_system_is_phone()
    {
        var config = new Dictionary<string, string> { ["system"] = "phone" };
        var result = new TelecomNormalizationNode().Execute("(313) 555-0142", config, null);
        ((System.Text.Json.Nodes.JsonObject)result.Value!)["value"]!.GetValue<string>().Should().Be("+13135550142");
    }

    [Fact]
    public void TelecomNormalizationNode_rejects_an_unrecognized_system()
    {
        var config = new Dictionary<string, string> { ["system"] = "bogus" };
        var result = new TelecomNormalizationNode().Execute("(313) 555-0142", config, null);
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("not a recognized ContactPoint system");
    }

    // An email value short-circuits before the override is consulted, so a forced fax/url cannot relabel it.
    [Fact]
    public void TelecomNormalizationNode_detects_email_even_when_a_system_override_is_set()
    {
        var config = new Dictionary<string, string> { ["system"] = "fax" };
        var result = new TelecomNormalizationNode().Execute("jane@example.com", config, null);
        ((System.Text.Json.Nodes.JsonObject)result.Value!)["system"]!.GetValue<string>().Should().Be("email");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("1.5")]
    [InlineData("abc")]
    [InlineData("99999999999999999999")]
    public void TelecomNormalizationNode_fails_for_a_rank_that_is_not_a_positive_int(string rank)
    {
        var config = new Dictionary<string, string> { ["rank"] = rank };
        var result = new TelecomNormalizationNode().Execute("(313) 555-0142", config, null);
        result.Success.Should().BeFalse();
    }

    // A blank rank means "not configured" — the key must be absent, not written as null.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TelecomNormalizationNode_omits_rank_when_it_is_blank(string rank)
    {
        var config = new Dictionary<string, string> { ["rank"] = rank };
        var result = new TelecomNormalizationNode().Execute("(313) 555-0142", config, null);
        ((System.Text.Json.Nodes.JsonObject)result.Value!).ContainsKey("rank").Should().BeFalse();
    }

    [Fact]
    public void TelecomNormalizationNode_skips_an_invalid_number_when_configured_to_skip()
    {
        var config = new Dictionary<string, string> { ["onInvalid"] = "skip" };
        var result = new TelecomNormalizationNode().Execute("123", config, null);
        result.Success.Should().BeTrue();
        result.Value.Should().BeNull();
    }

    [Fact]
    public void TelecomNormalizationNode_rejects_an_invalid_number_by_default()
    {
        var result = new TelecomNormalizationNode().Execute("123", new Dictionary<string, string>(), null);
        result.Success.Should().BeFalse();
    }

    [Fact]
    public void TelecomNormalizationNode_applies_the_regex_to_the_contact_value_before_parsing()
    {
        // The extension suffix is what makes this unparseable as-is; stripping it is the whole point of the option.
        var config = new Dictionary<string, string> { ["regexPattern"] = @"\s*(?:ext|x)\.?\s*\d+$" };
        var result = new TelecomNormalizationNode().Execute("(313) 555-0142 ext. 203", config, null);
        var contact = (System.Text.Json.Nodes.JsonObject)result.Value!;
        contact["value"]!.GetValue<string>().Should().Be("+13135550142");
    }

    [Fact]
    public void TelecomNormalizationNode_uses_the_regex_replacement_when_one_is_configured()
    {
        var config = new Dictionary<string, string> { ["regexPattern"] = "^Tel: ", ["regexReplacement"] = "+1 " };
        var result = new TelecomNormalizationNode().Execute("Tel: 3135550142", config, null);
        var contact = (System.Text.Json.Nodes.JsonObject)result.Value!;
        contact["value"]!.GetValue<string>().Should().Be("+13135550142");
    }

    [Fact]
    public void TelecomNormalizationNode_runs_the_regex_before_email_detection()
    {
        var config = new Dictionary<string, string> { ["regexPattern"] = @"^mailto:" };
        var result = new TelecomNormalizationNode().Execute("mailto:jane@example.com", config, null);
        var contact = (System.Text.Json.Nodes.JsonObject)result.Value!;
        contact["system"]!.GetValue<string>().Should().Be("email");
        contact["value"]!.GetValue<string>().Should().Be("jane@example.com");
    }

    [Fact]
    public void TelecomNormalizationNode_applies_the_regex_to_a_non_phone_system_value()
    {
        var config = new Dictionary<string, string> { ["system"] = "url", ["regexPattern"] = @"\s+", ["regexReplacement"] = "" };
        var result = new TelecomNormalizationNode().Execute("https://example.com/ contact", config, null);
        var contact = (System.Text.Json.Nodes.JsonObject)result.Value!;
        contact["value"]!.GetValue<string>().Should().Be("https://example.com/contact");
    }

    [Fact]
    public void TelecomNormalizationNode_treats_a_value_fully_erased_by_the_regex_as_blank()
    {
        var config = new Dictionary<string, string> { ["regexPattern"] = "^N/A$" };
        var result = new TelecomNormalizationNode().Execute("N/A", config, null);
        result.Success.Should().BeTrue();
        result.Value.Should().BeNull();
    }

    [Fact]
    public void TelecomNormalizationNode_fails_cleanly_on_an_invalid_regex_instead_of_throwing()
    {
        var config = new Dictionary<string, string> { ["regexPattern"] = "(unclosed" };
        var result = new TelecomNormalizationNode().Execute("(313) 555-0142", config, null);
        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void TelecomNormalizationNode_leaves_the_value_untouched_when_no_regex_is_configured()
    {
        var config = new Dictionary<string, string> { ["regexReplacement"] = "ignored-without-a-pattern" };
        var result = new TelecomNormalizationNode().Execute("(313) 555-0142", config, null);
        var contact = (System.Text.Json.Nodes.JsonObject)result.Value!;
        contact["value"]!.GetValue<string>().Should().Be("+13135550142");
    }
}
