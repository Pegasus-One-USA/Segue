using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services;
using FHIRBridge.Application.Services.Transforms;
using FHIRBridge.Application.Services.Transforms.Nodes;
using FHIRBridge.Domain.Enums;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Mapping;

/// <summary>
/// Covers joining several source fields into one destination column — the mapping canvas's multi-source row.
/// The payload here is the shape Epic actually sends: one Patient carrying the same person's name three times
/// (official/usual/nickname), where the given names fan out at a different rate than the family name.
/// </summary>
public sealed class JsonMappingEngineJoinedFieldsTests
{
    private readonly JsonMappingEngine _engine = new();

    private const string EpicPatientJson = """
    {
      "resourceType": "Patient",
      "name": [
        { "use": "official", "family": "McGinnis", "given": ["Warren", "James"] },
        { "use": "usual",    "family": "McGinnis", "given": ["Warren", "James"] },
        { "use": "nickname", "family": "McGinnis", "given": ["Warren"] }
      ]
    }
    """;

    private static MappingFieldDto JoinedNameField(string format, ArrayPolicy policy) => new(
        TargetField: "FullName",
        JsonPath: "$.name[*].given[*]|$.name[*].family",
        ValueType: MappingValueType.String,
        IsRequired: false,
        DefaultValue: null,
        Format: format,
        ArrayPolicy: policy);

    [Fact]
    public void Joining_given_and_family_keeps_both_fields_and_the_configured_delimiter()
    {
        // The whole point of the join: the family name must survive. Given names are repeats of ONE field, so
        // they join with a space; the configured delimiter separates the two distinct fields.
        var result = _engine.Map(EpicPatientJson, [JoinedNameField("joinedFields;delimiter=, ", ArrayPolicy.FirstItem)]);

        result.Errors.Should().BeEmpty();
        result.Rows.Should().ContainSingle();
        result.Rows![0]["FullName"].Should().Be("Warren James, McGinnis");
    }

    [Fact]
    public void A_sub_path_that_fans_out_faster_stays_aligned_with_its_own_array_instance()
    {
        // name[*].given[*] yields five matches, name[*].family three. Aligning those by flat position paired
        // "James" with the SECOND name's family and left the last two rows with no family at all.
        var result = _engine.Map(EpicPatientJson, [JoinedNameField("joinedFields;delimiter=, ", ArrayPolicy.RepeatParent)]);

        result.Errors.Should().BeEmpty();
        result.Rows.Should().HaveCount(3);
        result.Rows![0]["FullName"].Should().Be("Warren James, McGinnis");
        result.Rows[1]["FullName"].Should().Be("Warren James, McGinnis");
        result.Rows[2]["FullName"].Should().Be("Warren, McGinnis", "the third name entry has only one given name");
    }

    [Fact]
    public void The_delimiter_keeps_a_deliberate_trailing_space()
    {
        // "delimiter=, " used to be trimmed back to "," before it ever reached the join, so a user who typed
        // the most common delimiter of all got the one thing they didn't ask for.
        var result = _engine.Map(EpicPatientJson, [JoinedNameField("joinedFields;delimiter=, ", ArrayPolicy.FirstItem)]);
        result.Rows![0]["FullName"].Should().Be("Warren James, McGinnis").And.NotBe("Warren James,McGinnis");
    }

    [Fact]
    public void A_missing_sub_path_contributes_no_piece_rather_than_a_dangling_delimiter()
    {
        const string json = """{ "name": [ { "given": ["Warren", "James"] } ] }""";
        var result = _engine.Map(json, [JoinedNameField("joinedFields;delimiter=, ", ArrayPolicy.FirstItem)]);

        result.Rows![0]["FullName"].Should().Be("Warren James");
    }

    [Fact]
    public void A_sub_path_with_no_repeating_ancestor_joins_onto_every_instance()
    {
        const string json = """
        {
          "id": "pat-1",
          "name": [ { "family": "McGinnis" }, { "family": "Smith" } ]
        }
        """;

        var field = new MappingFieldDto(
            TargetField: "Label",
            JsonPath: "$.id|$.name[*].family",
            ValueType: MappingValueType.String,
            IsRequired: false,
            DefaultValue: null,
            Format: "joinedFields;delimiter=/",
            ArrayPolicy: ArrayPolicy.RepeatParent);

        var result = _engine.Map(json, [field]);

        result.Rows.Should().HaveCount(2);
        result.Rows![0]["Label"].Should().Be("pat-1/McGinnis");
        result.Rows[1]["Label"].Should().Be("pat-1/Smith", "a scalar sub-path belongs to every instance, not just the first");
    }

    [Fact]
    public void All_records_joins_each_instance_with_the_delimiter_and_the_instances_with_a_comma()
    {
        // The two levels of separation are NOT the same separator. A "full name" column sets its delimiter to a
        // space so given+family read as one name; the instances still have to be told apart, so they join with
        // ", " regardless. Using the configured delimiter for both ran all three names into one unreadable line.
        var result = _engine.Map(
            EpicPatientJson,
            [JoinedNameField("joinedFields;delimiter= ;aggregate=csv", ArrayPolicy.FirstItem)]);

        result.Rows.Should().ContainSingle();
        result.Rows![0]["FullName"].Should()
            .Be("Warren James McGinnis, Warren James McGinnis, Warren McGinnis");
    }

    [Fact]
    public void Nth_instance_selects_that_one_name_entry_from_a_joined_field()
    {
        // index=N is 0-based, so the portal's "Instance #2" arrives as index=1 — Epic's second name entry.
        var result = _engine.Map(
            EpicPatientJson,
            [JoinedNameField("joinedFields;delimiter= ;index=1", ArrayPolicy.FirstItem)]);

        result.Rows.Should().ContainSingle();
        result.Rows![0]["FullName"].Should().Be("Warren James McGinnis");
    }

    [Fact]
    public void Nth_instance_past_the_end_of_the_array_writes_null_rather_than_throwing()
    {
        var result = _engine.Map(
            EpicPatientJson,
            [JoinedNameField("joinedFields;delimiter= ;index=9", ArrayPolicy.FirstItem)]);

        result.Rows![0]["FullName"].Should().BeNull();
    }

    [Theory]
    // Catalog-supplied path for a source it knows (given fans out into two matches)...
    [InlineData("$.name[*].family|$.name[*].given[*]")]
    // ...and the path DERIVED for a source the catalog had no entry for, which lacks the inner wildcard and
    // so resolves to the whole ["Warren","James"] array. Both must write the same thing — which of the two a
    // source gets is invisible to the user and depends only on whether the catalog happened to know it.
    [InlineData("$.name[*].family|$.name[*].given")]
    public void An_unfanned_given_array_reads_the_same_as_a_wildcarded_one(string jsonPath)
    {
        var field = new MappingFieldDto(
            TargetField: "FullName",
            JsonPath: jsonPath,
            ValueType: MappingValueType.String,
            IsRequired: false, DefaultValue: null,
            Format: "joinedFields;delimiter=, ", ArrayPolicy: ArrayPolicy.FirstItem);

        var result = _engine.Map(EpicPatientJson, [field]);

        result.Rows![0]["FullName"].Should().Be("McGinnis, Warren James");
    }

    [Fact]
    public void Source_order_is_the_users_order()
    {
        // family-then-given must write family first. Order comes from the popover's own source list.
        var field = new MappingFieldDto(
            TargetField: "FullName",
            JsonPath: "$.name[*].family|$.name[*].given[*]",
            ValueType: MappingValueType.String,
            IsRequired: false, DefaultValue: null,
            Format: "joinedFields;delimiter=, ", ArrayPolicy: ArrayPolicy.FirstItem);

        _engine.Map(EpicPatientJson, [field]).Rows![0]["FullName"].Should().Be("McGinnis, Warren James");
    }

    [Fact]
    public void A_joined_column_hands_its_transform_chain_the_individual_source_values()
    {
        // ConcatenationTemplating binds {0}/{1} positionally to the items it receives, and AsItems treats a
        // string as ONE item — so against the joined string "{1}" matched nothing and survived as literal
        // text in the column ("Mr Warren James, McGinnis {1} Sir"). The parts make the template work.
        var result = _engine.Map(EpicPatientJson, [JoinedNameField("joinedFields;delimiter=, ", ArrayPolicy.FirstItem)]);

        result.RawArrayValues.Should().ContainKey("FullName");
        result.RawArrayValues!["FullName"].Should().Equal(["Warren James", "McGinnis"]);
    }

    [Fact]
    public void Nth_instance_hands_over_that_instances_parts_not_the_firsts()
    {
        var result = _engine.Map(
            EpicPatientJson,
            [JoinedNameField("joinedFields;delimiter=, ;index=2", ArrayPolicy.FirstItem)]);

        result.RawArrayValues!["FullName"].Should().Equal(["Warren", "McGinnis"]);
    }

    [Fact]
    public void All_records_keeps_the_collapsed_value_as_the_transform_input()
    {
        // The column deliberately spans every instance there, so there is no single pair of fields for a
        // template's placeholders to bind to.
        var result = _engine.Map(
            EpicPatientJson,
            [JoinedNameField("joinedFields;delimiter= ;aggregate=csv", ArrayPolicy.FirstItem)]);

        result.RawArrayValues!["FullName"].Should().NotEqual(["Warren James", "McGinnis"]);
    }

    [Fact]
    public void End_to_end_a_joined_column_with_a_saved_template_rule_renders_both_fields()
    {
        // Field config and rule config below are the EXACT rows read back out of a real workflow's database,
        // not an idealized version of them: jsonPath carries the derived "$.name[*].given" (no inner wildcard,
        // because the catalog had no entry for that source), and the rule is the portal's own saved ConfigJson.
        var field = new MappingFieldDto(
            TargetField: "FULLNAME",
            JsonPath: "$.name[*].given|$.name[*].family",
            ValueType: MappingValueType.String,
            IsRequired: false, DefaultValue: null,
            Format: "joinedFields;delimiter=, ", ArrayPolicy: ArrayPolicy.FirstItem);

        var mapped = _engine.Map(EpicPatientJson, [field]);

        // Untransformed, the column holds the joined string.
        mapped.Rows![0]["FULLNAME"].Should().Be("Warren James, McGinnis");

        // The rule resolves only because the joined path keys off its FIRST sub-path — normalizing the whole
        // "|"-joined string produced "Patient.name.given|$.name.family", which matched no rule and silently
        // left the column untransformed.
        RuleSourceFieldFormat.FromJsonPath("Patient", field.JsonPath).Should().Be("Patient.name.given");

        // And the chain is handed the field's PARTS, so both placeholders bind.
        var ruleConfig = new Dictionary<string, string>
        {
            ["mode"] = "concat",
            ["separator"] = " ",
            ["splitDelimiter"] = ",",
            ["splitIsRegex"] = "False",
            ["template"] = "Mr {0} {1} Sir",
        };
        var transformInput = mapped.RawArrayValues!["FULLNAME"];
        transformInput.Should().Equal(["Warren James", "McGinnis"]);

        new ConcatenationTemplatingNode().Execute(transformInput, ruleConfig, null)
            .Value.Should().Be("Mr Warren James McGinnis Sir");
    }

    [Fact]
    public void A_directField_aggregate_keeps_its_historical_comma_space_default()
    {
        // No delimiter marker at all (every directField) must keep writing exactly as it always has.
        var field = new MappingFieldDto(
            TargetField: "Given",
            JsonPath: "$.name[*].given[*]",
            ValueType: MappingValueType.String,
            IsRequired: false,
            DefaultValue: null,
            Format: "directField;aggregate=csv",
            ArrayPolicy: ArrayPolicy.FirstItem);

        var result = _engine.Map(EpicPatientJson, [field]);

        result.Rows![0]["Given"].Should().Be("Warren, James, Warren, James, Warren");
    }
}
