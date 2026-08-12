using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Enums;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Mapping;

/// <summary>Repro for a live bug report: a real Epic Condition resource's code.coding.code (4 coding entries,
/// ICD-10 first) maps to null via ArrayPolicy.FirstItem + "$.code[*].coding[*].code", even though the raw JSON
/// clearly has "J18.1" as the first coding's code.</summary>
public sealed class JsonMappingEngineConditionCodeReproTests
{
    private const string ConditionJson = """
        {"resourceType":"Condition","id":"ePJugUlqfIbIyVVibZWYmHQ3","clinicalStatus":{"coding":[{"system":"http://terminology.hl7.org/CodeSystem/condition-clinical","version":"4.0.0","code":"active","display":"Active"}],"text":"Active"},"verificationStatus":{"coding":[{"system":"http://terminology.hl7.org/CodeSystem/condition-ver-status","version":"4.0.0","code":"confirmed","display":"Confirmed"}],"text":"Confirmed"},"category":[{"coding":[{"system":"http://terminology.hl7.org/CodeSystem/condition-category","code":"problem-list-item","display":"Problem List Item"}],"text":"Problem List Item"}],"severity":{"text":"High"},"code":{"coding":[{"system":"http://hl7.org/fhir/sid/icd-10-cm","code":"J18.1","display":"Lobar pneumonia, unspecified organism"},{"system":"http://snomed.info/sct","code":"301004001","display":"RIGHT UPPER ZONE PNEUMONIA"},{"system":"http://hl7.org/fhir/sid/icd-9-cm","code":"486","display":"Right upper lobe pneumonia"},{"system":"urn:oid:2.16.840.1.113883.3.247.1.1","code":"802099","display":"Right upper lobe pneumonia"}],"text":"Right upper lobe pneumonia"},"subject":{"reference":"Patient/egqBHVfQlt4Bw3XGXoxVxHg3","display":"Davis, Elijah John"},"onsetDateTime":"2019-05-24","recordedDate":"2019-05-24"}
        """;

    [Fact]
    public void Malformed_path_with_an_extra_wildcard_on_the_non_repeating_code_object_finds_nothing()
    {
        // $.code[*]... treats "code" itself as an array, but Condition.code is a single object (0..1) — only
        // its nested "coding" repeats. This is what the live MappingField row actually has stored, and it's
        // why the real pipeline run wrote NULL: ResolveAll correctly finds zero matches for a non-existent array.
        var field = new MappingFieldDto(
            TargetField: "Code",
            JsonPath: "$.code[*].coding[*].code",
            ValueType: MappingValueType.String,
            IsRequired: false,
            DefaultValue: null,
            Format: null,
            ResourceType: "Condition",
            DestinationObject: "Condition",
            ArrayPolicy: ArrayPolicy.FirstItem);

        var result = new JsonMappingEngine().Map(ConditionJson, [field]);

        result.Values.Should().ContainKey("Code").WhoseValue.Should().BeNull("the malformed path resolves to zero matches");
    }

    [Fact]
    public void FirstItem_over_code_coding_code_extracts_the_first_ICD10_code()
    {
        var field = new MappingFieldDto(
            TargetField: "Code",
            JsonPath: "$.code.coding[*].code",
            ValueType: MappingValueType.String,
            IsRequired: false,
            DefaultValue: null,
            Format: null,
            ResourceType: "Condition",
            DestinationObject: "Condition",
            ArrayPolicy: ArrayPolicy.FirstItem);

        var result = new JsonMappingEngine().Map(ConditionJson, [field]);

        result.Values.Should().ContainKey("Code");
        result.Values["Code"].Should().Be("J18.1");
    }
}
