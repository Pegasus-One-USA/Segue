using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Enums;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Mapping;

/// <summary>
/// <c>Patient.contact</c> is sparse: one entry may carry only a name, another only a relationship/telecom/address,
/// with no field shared across every entry. <see cref="ArrayPolicy.RepeatParent"/> aligns rows by "position in the
/// filtered match list", which scrambles data across rows when a field is missing from some array elements.
/// <see cref="ArrayPolicy.SeparateDestination"/> instead keys each match by its real array index, so it stays
/// correct under sparse data — this is why the emergency-contact mapping profile uses it.
/// </summary>
public sealed class JsonMappingEngineArrayPolicyTests
{
    private const string PatientJson = """
        {
          "resourceType": "Patient",
          "id": "eTjDDWfopD0BnRlyEO2mGZQ3",
          "contact": [
            { "name": { "use": "usual", "text": "No,Contact" } },
            {
              "relationship": [ { "coding": [ { "system": "http://terminology.hl7.org/CodeSystem/v2-0131", "code": "E", "display": "Employer" } ] } ],
              "telecom": [ { "system": "phone", "value": "608-271-9000", "use": "work" } ],
              "organization": { "display": "Ehs Generic Employer" }
            }
          ]
        }
        """;

    private static readonly JsonMappingEngine Sut = new();

    private static MappingFieldDto Field(string target, string jsonPath) => new(
        TargetField: target,
        JsonPath: jsonPath,
        ValueType: MappingValueType.String,
        IsRequired: false,
        DefaultValue: null,
        Format: null,
        ResourceType: "Patient",
        DestinationObject: "dbo.PatientEmergencyContact",
        ArrayPolicy: ArrayPolicy.SeparateDestination,
        ArrayAncestors: ["contact"]);

    [Fact]
    public void SeparateDestination_keeps_sparse_contact_fields_on_their_own_row()
    {
        var fields = new[]
        {
            Field("ContactName", "$.contact[*].name.text"),
            Field("RelationshipDisplay", "$.contact[*].relationship[0].coding[0].display"),
        };

        var result = Sut.Map(PatientJson, fields);

        result.ChildTables.Should().ContainSingle(t => t.Name == "dbo.PatientEmergencyContact");
        var rows = result.ChildTables!.Single().Rows;

        rows.Should().HaveCount(2);
        rows.Should().ContainSingle(r => (string?)r.GetValueOrDefault("ContactName") == "No,Contact");
        rows.Should().ContainSingle(r => (string?)r.GetValueOrDefault("RelationshipDisplay") == "Employer");

        // The row that got the name must NOT also have picked up the other contact's relationship (the bug
        // RepeatParent would hit: both fields would land on row 0 since each has only one match).
        var nameRow = rows.Single(r => r.ContainsKey("ContactName"));
        nameRow.GetValueOrDefault("RelationshipDisplay").Should().BeNull();
    }

    /// <summary>
    /// <c>Patient.identifier[]</c> typically carries several identifiers (MRN, CSN, an Epic-internal id, …), each
    /// disambiguated only by its <c>system</c> — often an opaque, tenant-specific OID (e.g.
    /// <c>urn:oid:1.2.840.114350.1.72.1.7.7.10.696784.13260</c> for a given Epic install's internal id). Positional
    /// array access can't reliably pick "the MRN" vs "the internal id" since order isn't guaranteed across records —
    /// <see cref="ArrayPolicy.CorrelateByCode"/> (originally built for <c>Observation.component[]</c> LOINC codes)
    /// is generic enough to reuse here: correlate on the sibling <c>system</c> element instead of a sibling code.
    /// </summary>
    [Fact]
    public void CorrelateByCode_selects_identifier_value_by_sibling_system_oid()
    {
        const string patientJson = """
            {
              "resourceType": "Patient",
              "id": "eTjDDWfopD0BnRlyEO2mGZQ3",
              "identifier": [
                { "system": "urn:oid:1.2.840.114350.1.13.0.1.7.3.688884.100", "value": "MRN-12345" },
                { "system": "urn:oid:1.2.840.114350.1.72.1.7.7.10.696784.13260", "value": "738U002" }
              ]
            }
            """;

        var field = new MappingFieldDto(
            TargetField: "EpicInternalId",
            JsonPath: "$.identifier[*].value",
            ValueType: MappingValueType.String,
            IsRequired: false,
            DefaultValue: null,
            Format: null,
            ResourceType: "Patient",
            ArrayPolicy: ArrayPolicy.CorrelateByCode,
            CorrelationCodeJsonPath: "$.identifier[*].system",
            CorrelationCodeValue: "urn:oid:1.2.840.114350.1.72.1.7.7.10.696784.13260");

        var result = Sut.Map(patientJson, [field]);

        result.Errors.Should().BeEmpty();
        result.Values.GetValueOrDefault("EpicInternalId").Should().Be("738U002");
    }

    [Fact]
    public void CorrelateByCode_with_no_matching_system_yields_null_not_an_error()
    {
        const string patientJson = """
            {
              "resourceType": "Patient",
              "id": "eTjDDWfopD0BnRlyEO2mGZQ3",
              "identifier": [
                { "system": "urn:oid:1.2.840.114350.1.13.0.1.7.3.688884.100", "value": "MRN-12345" }
              ]
            }
            """;

        var field = new MappingFieldDto(
            TargetField: "EpicInternalId",
            JsonPath: "$.identifier[*].value",
            ValueType: MappingValueType.String,
            IsRequired: false,
            DefaultValue: null,
            Format: null,
            ResourceType: "Patient",
            ArrayPolicy: ArrayPolicy.CorrelateByCode,
            CorrelationCodeJsonPath: "$.identifier[*].system",
            CorrelationCodeValue: "urn:oid:1.2.840.114350.1.72.1.7.7.10.696784.13260");

        var result = Sut.Map(patientJson, [field]);

        result.Errors.Should().BeEmpty();
        result.Values.GetValueOrDefault("EpicInternalId").Should().BeNull();
    }

    /// <summary>
    /// The field-mapping canvas's "Match criteria" instance selection (field-mapping-model.ts's
    /// resolveArrayPolicy, 'criteria' with op "=") reuses this exact mechanism: the criteria's plain field
    /// name becomes CorrelationCodeJsonPath (a sibling element sharing the mapped field's own outermost
    /// array index), and its typed value becomes CorrelationCodeValue. No engine changes were needed to
    /// support it — this closes the loop on that specific real-world case (picking a Patient's mobile
    /// phone number by its telecom.use, rather than by array position).
    /// </summary>
    [Fact]
    public void CorrelateByCode_selects_a_telecom_value_by_sibling_use_criteria()
    {
        const string patientJson = """
            {
              "resourceType": "Patient",
              "telecom": [
                { "system": "phone", "value": "897-532-5871", "use": "home" },
                { "system": "phone", "value": "+1(585)-998-7425", "use": "mobile" },
                { "system": "phone", "value": "987-452-2522", "use": "work" }
              ]
            }
            """;

        var field = new MappingFieldDto(
            TargetField: "Telecom",
            JsonPath: "$.telecom[*].value",
            ValueType: MappingValueType.String,
            IsRequired: false,
            DefaultValue: null,
            Format: null,
            ResourceType: "Patient",
            ArrayPolicy: ArrayPolicy.CorrelateByCode,
            CorrelationCodeJsonPath: "$.telecom[*].use",
            CorrelationCodeValue: "mobile");

        var result = Sut.Map(patientJson, [field]);

        result.Errors.Should().BeEmpty();
        result.Values.GetValueOrDefault("Telecom").Should().Be("+1(585)-998-7425");
    }

    private const string TelecomJson = """
        {
          "resourceType": "Patient",
          "telecom": [
            { "system": "phone", "value": "897-532-5871", "use": "home" },
            { "system": "phone", "value": "+1(585)-998-7425", "use": "mobile" },
            { "system": "phone", "value": "987-452-2522", "use": "work" }
          ]
        }
        """;

    private static MappingFieldDto TelecomField(string? correlationCodeOperator, string correlationCodeValue) => new(
        TargetField: "Telecom",
        JsonPath: "$.telecom[*].value",
        ValueType: MappingValueType.String,
        IsRequired: false,
        DefaultValue: null,
        Format: null,
        ResourceType: "Patient",
        ArrayPolicy: ArrayPolicy.CorrelateByCode,
        CorrelationCodeJsonPath: "$.telecom[*].use",
        CorrelationCodeValue: correlationCodeValue,
        CorrelationCodeOperator: correlationCodeOperator);

    [Fact]
    public void CorrelationCodeOperator_null_still_behaves_as_exact_equals_for_every_field_saved_before_the_operator_existed()
    {
        var result = Sut.Map(TelecomJson, [TelecomField(correlationCodeOperator: null, correlationCodeValue: "mobile")]);

        result.Errors.Should().BeEmpty();
        result.Values.GetValueOrDefault("Telecom").Should().Be("+1(585)-998-7425");
    }

    [Fact]
    public void CorrelationCodeOperator_Contains_matches_a_sibling_value_by_substring()
    {
        // "mob" is a substring of "mobile" but not an exact match — Equals would find nothing here.
        var result = Sut.Map(TelecomJson, [TelecomField(correlationCodeOperator: "Contains", correlationCodeValue: "mob")]);

        result.Errors.Should().BeEmpty();
        result.Values.GetValueOrDefault("Telecom").Should().Be("+1(585)-998-7425");
    }

    [Fact]
    public void CorrelationCodeOperator_NotEquals_matches_the_first_sibling_that_differs()
    {
        // "home" (the first telecom entry) is excluded; "mobile" (the second) is the first that qualifies.
        var result = Sut.Map(TelecomJson, [TelecomField(correlationCodeOperator: "NotEquals", correlationCodeValue: "home")]);

        result.Errors.Should().BeEmpty();
        result.Values.GetValueOrDefault("Telecom").Should().Be("+1(585)-998-7425");
    }

    [Fact]
    public void CorrelationCodeOperator_NotEquals_with_no_qualifying_sibling_yields_null_not_an_error()
    {
        // Every telecom entry shares the SAME "use" — "not equal to that same value" has nothing left to match.
        const string singleUseJson = """
            {
              "resourceType": "Patient",
              "telecom": [
                { "system": "phone", "value": "897-532-5871", "use": "home" },
                { "system": "phone", "value": "987-452-2522", "use": "home" }
              ]
            }
            """;

        var result = Sut.Map(singleUseJson, [TelecomField(correlationCodeOperator: "NotEquals", correlationCodeValue: "home")]);

        result.Errors.Should().BeEmpty();
        result.Values.GetValueOrDefault("Telecom").Should().BeNull();
    }

    /// <summary>
    /// Regression guard for a real reported bug: correlating on a TWO-level nested repeating structure
    /// (Patient.contact[] containing its own telecom[]) matched only on the outer "which contact" index,
    /// so a "system contains mail" criteria on the second contact's telecom[1].value returned that contact's
    /// FIRST telecom value (telecom[0], a phone number) instead of the one whose OWN system actually matched.
    /// The fix correlates on the full index chain (both the contact AND the telecom index), not just the
    /// first level — see JsonMappingEngine.IsIndexChainMatch.
    /// </summary>
    [Fact]
    public void CorrelateByCode_on_a_two_level_nested_array_matches_the_specific_inner_item_not_just_the_outer_one()
    {
        const string patientJson = """
            {
              "resourceType": "Patient",
              "contact": [
                {
                  "name": { "text": "Test Postcare" },
                  "telecom": [
                    { "system": "phone", "value": "125-875-4714", "use": "home" },
                    { "system": "email", "value": "Postcare123@gmail.com" }
                  ]
                },
                {
                  "name": { "text": "Test test" },
                  "telecom": [
                    { "system": "phone", "value": "897-532-5871", "use": "home" },
                    { "system": "phone", "value": "897-532-5871", "use": "mobile" }
                  ]
                }
              ]
            }
            """;

        var field = new MappingFieldDto(
            TargetField: "Telecom",
            JsonPath: "$.contact[*].telecom[*].value",
            ValueType: MappingValueType.String,
            IsRequired: false,
            DefaultValue: null,
            Format: null,
            ResourceType: "Patient",
            ArrayPolicy: ArrayPolicy.CorrelateByCode,
            CorrelationCodeJsonPath: "$.contact[*].telecom[*].system",
            CorrelationCodeValue: "mail",
            CorrelationCodeOperator: "Contains");

        var result = Sut.Map(patientJson, [field]);

        result.Errors.Should().BeEmpty();
        result.Values.GetValueOrDefault("Telecom").Should().Be("Postcare123@gmail.com");
    }

    /// <summary>
    /// The exact reported live scenario: correlating on "system" NotEquals "mail" — since no telecom entry's
    /// system is literally "mail" (they're "phone"/"phone"/"email"/"phone"), every one of them qualifies, and
    /// the engine must return the FIRST field-order match (contact[0].telecom[0]), not null.
    /// </summary>
    [Fact]
    public void CorrelateByCode_NotEquals_matching_every_sibling_returns_the_first_one_not_null()
    {
        const string patientJson = """
            {
              "resourceType": "Patient",
              "contact": [
                {
                  "telecom": [
                    { "system": "phone", "value": "125-875-4714", "use": "home" },
                    { "system": "email", "value": "Postcare123@gmail.com" }
                  ]
                },
                {
                  "telecom": [
                    { "system": "phone", "value": "897-532-5871", "use": "home" },
                    { "system": "phone", "value": "897-532-5871-mobile", "use": "mobile" }
                  ]
                }
              ]
            }
            """;

        var field = new MappingFieldDto(
            TargetField: "Telecom",
            JsonPath: "$.contact[*].telecom[*].value",
            ValueType: MappingValueType.String,
            IsRequired: false,
            DefaultValue: null,
            Format: null,
            ResourceType: "Patient",
            ArrayPolicy: ArrayPolicy.CorrelateByCode,
            CorrelationCodeJsonPath: "$.contact[*].telecom[*].system",
            CorrelationCodeValue: "mail",
            CorrelationCodeOperator: "NotEquals");

        var result = Sut.Map(patientJson, [field]);

        result.Errors.Should().BeEmpty();
        result.Values.GetValueOrDefault("Telecom").Should().Be("125-875-4714");
    }

    /// <summary>
    /// Same two-level nesting as the Contains regression above, but with NotEquals — proves the full-index-
    /// chain fix (IsIndexChainMatch) isn't specific to one operator. contact[0].telecom[0].use and
    /// contact[1].telecom[0].use are both "home" (excluded); contact[0].telecom[1] (the email entry) has no
    /// "use" at all, so it never becomes a candidate match either way. Only contact[1].telecom[1].use
    /// ("mobile") qualifies — its OWN value must be returned, not contact[1]'s first telecom (which is the
    /// "home" one that was just excluded).
    /// </summary>
    [Fact]
    public void CorrelateByCode_NotEquals_on_a_two_level_nested_array_matches_the_specific_inner_item()
    {
        const string patientJson = """
            {
              "resourceType": "Patient",
              "contact": [
                {
                  "name": { "text": "Test Postcare" },
                  "telecom": [
                    { "system": "phone", "value": "125-875-4714", "use": "home" },
                    { "system": "email", "value": "Postcare123@gmail.com" }
                  ]
                },
                {
                  "name": { "text": "Test test" },
                  "telecom": [
                    { "system": "phone", "value": "897-532-5871", "use": "home" },
                    { "system": "phone", "value": "897-532-5871-mobile", "use": "mobile" }
                  ]
                }
              ]
            }
            """;

        var field = new MappingFieldDto(
            TargetField: "Telecom",
            JsonPath: "$.contact[*].telecom[*].value",
            ValueType: MappingValueType.String,
            IsRequired: false,
            DefaultValue: null,
            Format: null,
            ResourceType: "Patient",
            ArrayPolicy: ArrayPolicy.CorrelateByCode,
            CorrelationCodeJsonPath: "$.contact[*].telecom[*].use",
            CorrelationCodeValue: "home",
            CorrelationCodeOperator: "NotEquals");

        var result = Sut.Map(patientJson, [field]);

        result.Errors.Should().BeEmpty();
        result.Values.GetValueOrDefault("Telecom").Should().Be("897-532-5871-mobile");
    }
}
