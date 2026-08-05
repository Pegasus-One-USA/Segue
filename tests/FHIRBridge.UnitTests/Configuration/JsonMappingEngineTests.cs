using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Enums;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Configuration;

/// <summary>
/// Covers the two JsonMappingEngine gaps fixed alongside the Mapping Config Import wiring: a `[*]`-fanned
/// JsonPath actually resolving every array item (rather than the array element itself), and the "joinedFields"
/// column mode (multiple `|`-delimited sub-paths joined into one value per row) being interpreted at all.
/// </summary>
public sealed class JsonMappingEngineTests
{
    private readonly JsonMappingEngine _engine = new();

    [Fact]
    public void Bracketed_wildcard_path_fans_out_over_a_repeating_array()
    {
        const string json = """
            {
              "resourceType": "Patient",
              "name": [
                { "family": "Smith" },
                { "family": "Jones" }
              ]
            }
            """;

        var field = new MappingFieldDto(
            TargetField: "family",
            JsonPath: "$.name[*].family",
            ValueType: MappingValueType.String,
            IsRequired: false,
            DefaultValue: null,
            Format: "directField",
            ArrayPolicy: ArrayPolicy.RepeatParent);

        var result = _engine.Map(json, [field]);

        result.Errors.Should().BeEmpty();
        result.Rows.Should().HaveCount(2);
        result.Rows![0]["family"].Should().Be("Smith");
        result.Rows[1]["family"].Should().Be("Jones");
    }

    [Fact]
    public void Unbracketed_path_against_an_array_does_not_fan_out()
    {
        // Regression guard for the bug this fix addresses: a plain (non-`[*]`) segment against a JSON array
        // returns the array element itself, which then fails to resolve the next segment — zero matches.
        const string json = """{ "name": [ { "family": "Smith" } ] }""";

        var field = new MappingFieldDto(
            TargetField: "family",
            JsonPath: "$.name.family",
            ValueType: MappingValueType.String,
            IsRequired: false,
            DefaultValue: null,
            Format: "directField");

        var result = _engine.Map(json, [field]);

        result.Errors.Should().BeEmpty();
        result.Values["family"].Should().BeNull();
    }

    [Fact]
    public void JoinedFields_joins_each_rows_sub_paths_with_the_configured_delimiter()
    {
        const string json = """
            {
              "contact": [
                { "address": { "city": "Springfield", "district": "D1" } },
                { "address": { "city": "Shelbyville", "district": "D2" } }
              ]
            }
            """;

        var field = new MappingFieldDto(
            TargetField: "AddressCombo",
            JsonPath: "$.contact[*].address.city|$.contact[*].address.district",
            ValueType: MappingValueType.String,
            IsRequired: false,
            DefaultValue: null,
            Format: "joinedFields;delimiter=,",
            ArrayPolicy: ArrayPolicy.RepeatParent);

        var result = _engine.Map(json, [field]);

        result.Errors.Should().BeEmpty();
        result.Rows.Should().HaveCount(2);
        result.Rows![0]["AddressCombo"].Should().Be("Springfield,D1");
        result.Rows[1]["AddressCombo"].Should().Be("Shelbyville,D2");
    }

    [Fact]
    public void JoinedFields_with_no_matches_falls_back_to_default_or_null()
    {
        const string json = """{ "contact": [] }""";

        var field = new MappingFieldDto(
            TargetField: "AddressCombo",
            JsonPath: "$.contact[*].address.city|$.contact[*].address.district",
            ValueType: MappingValueType.String,
            IsRequired: false,
            DefaultValue: null,
            Format: "joinedFields;delimiter=,");

        var result = _engine.Map(json, [field]);

        result.Errors.Should().BeEmpty();
        result.Values["AddressCombo"].Should().BeNull();
    }

    [Fact]
    public void A_SeparateDestination_field_with_zero_matches_never_leaks_into_the_parent_row()
    {
        // Regression guard for a real production bug: "period.end" is an optional sub-field FHIR often omits
        // (e.g. an ongoing address has no end date). Before the fix, a zero-match SeparateDestination field
        // fell through to `parent[field.TargetField] = null`, polluting the PARENT record with a column that
        // only exists on the child table — causing "Invalid column name 'periodEndDate'" against the root table.
        const string json = """
            {
              "address": [
                { "city": "Springfield", "period": { "start": "2019-05-24" } }
              ]
            }
            """;

        var fields = new[]
        {
            new MappingFieldDto("city", "$.address[*].city", MappingValueType.String, IsRequired: false,
                DefaultValue: null, Format: "directField", DestinationObject: "PatientAddress", ArrayPolicy: ArrayPolicy.SeparateDestination),
            new MappingFieldDto("periodEndDate", "$.address[*].period.end", MappingValueType.String, IsRequired: false,
                DefaultValue: null, Format: "directField", DestinationObject: "PatientAddress", ArrayPolicy: ArrayPolicy.SeparateDestination),
        };

        var result = _engine.Map(json, fields);

        result.Errors.Should().BeEmpty();
        result.Values.Should().NotContainKey("periodEndDate");
        result.ChildTables.Should().ContainSingle(t => t.Name == "PatientAddress");
        var row = result.ChildTables!.Single().Rows.Single();
        row["city"].Should().Be("Springfield");
        row.Should().NotContainKey("periodEndDate", "the field had zero matches on this occurrence, so it contributes nothing rather than a spurious null column");
    }

    [Fact]
    public void A_reference_field_extracts_the_resource_local_id_and_clears_the_raw_value()
    {
        // Regression coverage for the Observation -> Patient FK gap: "$.subject.reference" resolves to
        // "Patient/eAB3mDIBBcyUKviyzrxsnAw", which is never itself a valid value for a bigint FK column — the
        // engine must extract just the id and hand the raw reference off for the WRITER to resolve, rather than
        // leaving the un-resolvable string sitting in Values.
        const string json = """{ "subject": { "reference": "Patient/eAB3mDIBBcyUKviyzrxsnAw", "display": "Jane Doe" } }""";

        var field = new MappingFieldDto(
            TargetField: "PatientId",
            JsonPath: "$.subject.reference",
            ValueType: MappingValueType.String,
            IsRequired: false,
            DefaultValue: null,
            Format: "directField",
            ReferenceLookupTable: "Patient",
            ReferenceLookupKeyColumn: "PatientId");

        var result = _engine.Map(json, [field]);

        result.Errors.Should().BeEmpty();
        result.Values["PatientId"].Should().BeNull("the raw reference string is never a valid value for the target column — the writer resolves it");
        result.ReferenceLookups.Should().ContainSingle();
        var lookup = result.ReferenceLookups!.Single();
        lookup.TargetField.Should().Be("PatientId");
        lookup.LookupTable.Should().Be("Patient");
        lookup.LookupKeyColumn.Should().Be("PatientId");
        lookup.ReferenceId.Should().Be("eAB3mDIBBcyUKviyzrxsnAw");
    }

    [Fact]
    public void A_reference_field_with_no_reference_present_yields_a_null_reference_id_not_an_error()
    {
        const string json = """{ "status": "final" }""";

        var field = new MappingFieldDto(
            TargetField: "PatientId",
            JsonPath: "$.subject.reference",
            ValueType: MappingValueType.String,
            IsRequired: false,
            DefaultValue: null,
            Format: "directField",
            ReferenceLookupTable: "Patient",
            ReferenceLookupKeyColumn: "PatientId");

        var result = _engine.Map(json, [field]);

        result.Errors.Should().BeEmpty();
        result.Values["PatientId"].Should().BeNull();
        // No reference lookup is even recorded — there's nothing to resolve, so the writer leaves the column
        // as whatever's already there and lets a NOT NULL constraint fail with its own clear message.
        (result.ReferenceLookups ?? []).Should().BeEmpty();
    }
}
