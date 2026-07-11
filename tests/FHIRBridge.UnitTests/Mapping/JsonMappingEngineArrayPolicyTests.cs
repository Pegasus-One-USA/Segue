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
}
