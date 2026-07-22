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
}
