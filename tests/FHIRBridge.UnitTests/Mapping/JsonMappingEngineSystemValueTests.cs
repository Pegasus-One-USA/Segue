using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Enums;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Mapping;

/// <summary>
/// A field whose JsonPath is a reserved <c>@token</c> is a "system value" — its value comes from the pipeline/runtime
/// context (run id, write time, resource type, source id) passed via <c>systemValues</c>, not from the source FHIR
/// document. This lets audit/lineage columns be declared and filled like any normal mapped field, with no
/// special-casing in the destination writers.
/// </summary>
public sealed class JsonMappingEngineSystemValueTests
{
    private static readonly JsonMappingEngine Sut = new();

    private const string PatientJson = """{ "resourceType": "Patient", "id": "erXuFYUfucBZaryVksYEcMg3" }""";

    private static MappingFieldDto Field(string target, string jsonPath, MappingValueType type = MappingValueType.String) =>
        new(target, jsonPath, type, IsRequired: false, DefaultValue: null, Format: null);

    [Fact]
    public void System_token_fields_resolve_from_system_values_not_the_document()
    {
        var runId = Guid.NewGuid();
        var writtenOn = new DateTime(2026, 7, 22, 5, 27, 13, DateTimeKind.Utc);
        var systemValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["@runId"] = runId,
            ["@now"] = writtenOn,
            ["@resourceType"] = "Patient",
        };

        var fields = new[]
        {
            Field("SourceResourceId", "$.id"),           // normal JSONPath field
            Field("PipelineRunId", "@runId"),            // system value
            Field("ResourceType", "@resourceType"),      // system value
            Field("WrittenOnUtc", "@now", MappingValueType.DateTime),
        };

        var result = Sut.Map(PatientJson, fields, systemValues);

        result.Errors.Should().BeEmpty();
        result.Values["SourceResourceId"].Should().Be("erXuFYUfucBZaryVksYEcMg3");
        result.Values["PipelineRunId"].Should().Be(runId);
        result.Values["ResourceType"].Should().Be("Patient");
        result.Values["WrittenOnUtc"].Should().Be(writtenOn);
    }

    [Fact]
    public void Unknown_or_absent_system_token_maps_to_null()
    {
        var fields = new[] { Field("PipelineRunId", "@runId") };

        // No systemValues supplied at all.
        var result = Sut.Map(PatientJson, fields);

        result.Values.Should().ContainKey("PipelineRunId");
        result.Values["PipelineRunId"].Should().BeNull();
    }

    [Fact]
    public void At_default_token_writes_the_literal_DefaultValue_ignoring_the_source_document()
    {
        var field = new MappingFieldDto("ClientType", "@default", MappingValueType.String, IsRequired: false, DefaultValue: "Patient", Format: null);

        var result = Sut.Map(PatientJson, [field]);

        result.Errors.Should().BeEmpty();
        result.Values["ClientType"].Should().Be("Patient");
    }

    [Fact]
    public void At_default_token_with_no_DefaultValue_configured_records_an_error_and_writes_null()
    {
        // A save-time guarantee (CreateMappingProfileRequestValidator) requires DefaultValue whenever the
        // token is "@default" — this covers a profile saved before that rule existed reaching the engine
        // with none, which must fail loudly (never fall back to resolving the token against the document,
        // which is what silently wrote the whole raw resource JSON into the column before this guard existed).
        var field = new MappingFieldDto("ClientType", "@default", MappingValueType.String, IsRequired: false, DefaultValue: null, Format: null);

        var result = Sut.Map(PatientJson, [field]);

        result.Errors.Should().ContainSingle();
        result.Values["ClientType"].Should().BeNull();
    }

    [Fact]
    public void At_destinationObject_token_prefers_the_FIELD_own_DestinationObject_over_the_profile_level_value()
    {
        // A field routed to a child/extra table (e.g. Patient.name fanned out onto dbo.PatientName) must
        // report ITS OWN table for "@destinationObject", not the mapping profile's primary table the
        // run-level systemValues dictionary carries.
        var childField = new MappingFieldDto(
            "TableName", "@destinationObject", MappingValueType.String, IsRequired: false, DefaultValue: null, Format: null,
            DestinationObject: "dbo.PatientName");
        var rootField = new MappingFieldDto(
            "TableName", "@destinationObject", MappingValueType.String, IsRequired: false, DefaultValue: null, Format: null);
        var systemValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["@destinationObject"] = "dbo.Patient",
        };

        Sut.Map(PatientJson, [childField], systemValues).Values["TableName"].Should().Be("dbo.PatientName");
        Sut.Map(PatientJson, [rootField], systemValues).Values["TableName"].Should().Be("dbo.Patient");
    }

    [Fact]
    public void At_newGuid_token_resolves_from_systemValues_like_any_other_system_token()
    {
        var freshGuid = Guid.NewGuid();
        var field = Field("SurrogateId", "@newGuid");
        var systemValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["@newGuid"] = freshGuid };

        var result = Sut.Map(PatientJson, [field], systemValues);

        result.Values["SurrogateId"].Should().Be(freshGuid);
    }
}
