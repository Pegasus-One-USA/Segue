using System.Text.Json;
using FHIRBridge.Application.Services.Transforms;
using FHIRBridge.Application.Services.Transforms.Nodes;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FluentAssertions;
using Moq;

namespace FHIRBridge.UnitTests.Transforms;

/// <summary>
/// End-to-end coverage of the FHIR-resource transform loop: read a value at a rule's FHIR path, run it through
/// the shared 20-node registry, write the result back into the resource's own JSON.
///
/// This is the only path by which a transformation reaches a FHIR-native destination (Aidbox/Medplum/Azure
/// FHIR), which stores whole resources and has no mapped columns.
/// </summary>
public sealed class FhirResourceTransformServiceTests
{
    private const string ObservationJson = """
        {
          "resourceType": "Observation",
          "id": "obs-1",
          "status": "final",
          "valueQuantity": { "value": 70.30681818, "unit": "kg" },
          "code": { "coding": [ { "code": "29463-7", "display": "Body weight" } ] }
        }
        """;

    private static TransformationRule Rule(
        TransformNodeType nodeType,
        string sourceField,
        object config,
        string? writeBackPath = null,
        TransformScope scope = TransformScope.ResourceType,
        int order = 0,
        TransformErrorPolicy errorPolicy = TransformErrorPolicy.NullOut) =>
        new(
            scope,
            nodeType,
            JsonSerializer.Serialize(config),
            resourceType: "Observation",
            sourceField: sourceField,
            order: order,
            errorPolicy: errorPolicy,
            fhirWriteBackJsonPath: writeBackPath,
            executionPhase: TransformExecutionPhase.FhirResource);

    /// <summary>Stubs the resolver directly so these tests exercise the transform loop, not tier precedence
    /// (which <see cref="FhirResourceRuleResolverTests"/> covers).</summary>
    private static FhirResourceTransformService CreateSut(params TransformationRule[] rules)
    {
        var resolver = new Mock<IFhirResourceRuleResolver>();

        resolver
            .Setup(r => r.ResolveSourceFieldsAsync(
                It.IsAny<string>(), It.IsAny<DestinationType>(), It.IsAny<Guid?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(rules.Select(rule => rule.SourceField!).Distinct().ToList());

        resolver
            .Setup(r => r.ResolveAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DestinationType>(), It.IsAny<Guid?>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string sourceField, DestinationType _, Guid? _, string? _, CancellationToken _) =>
                rules.Where(rule => rule.SourceField == sourceField).OrderBy(rule => rule.Order).ToList());

        return new FhirResourceTransformService(resolver.Object, BuildNodeRegistry());
    }

    /// <summary>The real registry over the real nodes — the whole point is that a rule behaves identically here
    /// and on the mapped-column path, so stubbing the nodes would test nothing worth testing.</summary>
    private static ITransformNodeRegistry BuildNodeRegistry() =>
        new TransformNodeRegistry(
        [
            new RoundingScalingNode(),
            new StringNormalizationNode(),
            new DefaultNullHandlingNode(),
            new QuantityRangeAssemblyNode(),
        ]);

    private static async Task<FhirResourceTransformResult> RunAsync(FhirResourceTransformService sut) =>
        await sut.TransformAsync(
            ObservationJson, "Observation", "obs-1", DestinationType.Medplum, null, "Epic", default);

    [Fact]
    public async Task Writes_a_transformed_value_back_into_the_resources_own_json()
    {
        var sut = CreateSut(Rule(
            TransformNodeType.RoundingScaling,
            "Observation.valueQuantity.value",
            new { decimalPlaces = "2" }));

        var result = await RunAsync(sut);

        var document = JsonDocument.Parse(result.Json);
        document.RootElement.GetProperty("valueQuantity").GetProperty("value").GetDouble()
            .Should().Be(70.31);

        // Everything the rule didn't name survives untouched — this is an in-place edit, not a projection.
        document.RootElement.GetProperty("status").GetString().Should().Be("final");
        document.RootElement.GetProperty("valueQuantity").GetProperty("unit").GetString().Should().Be("kg");
        document.RootElement.GetProperty("id").GetString().Should().Be("obs-1");
    }

    [Fact]
    public async Task Quantity_assembly_keeps_the_sources_own_unit_on_the_FhirResource_path()
    {
        // The headline bug, on THIS execution path rather than the Runtime pipeline's mapped-column loop.
        // The rule reads "$.valueQuantity.value", so the node is handed the scalar 70.30681818 and its
        // whole-Quantity branch never fires — leaving the rule's own (unset) unit as the only candidate,
        // which is how an assembled Quantity came out as "unit": "". It matters more here than on the
        // mapped-column path: the write-back replaces the whole element, so the source's "kg" is not merely
        // missed, it is ERASED from a FHIR-native destination.
        var sut = CreateSut(Rule(
            TransformNodeType.QuantityRangeAssembly,
            "Observation.valueQuantity.value",
            new { },
            writeBackPath: "valueQuantity"));

        var result = await RunAsync(sut);

        var quantity = JsonDocument.Parse(result.Json).RootElement.GetProperty("valueQuantity");
        quantity.GetProperty("unit").GetString().Should().Be("kg");
        quantity.GetProperty("value").GetDouble().Should().Be(70.30681818);
    }

    [Fact]
    public async Task An_explicitly_configured_unit_still_wins_over_the_sources_own()
    {
        // The hint is a FALLBACK, not an override — a rule that names its unit is making a deliberate
        // choice (a conversion, or a source whose unit is wrong) and must not be second-guessed.
        var sut = CreateSut(Rule(
            TransformNodeType.QuantityRangeAssembly,
            "Observation.valueQuantity.value",
            new { unit = "lb" },
            writeBackPath: "valueQuantity"));

        var result = await RunAsync(sut);

        JsonDocument.Parse(result.Json).RootElement
            .GetProperty("valueQuantity").GetProperty("unit").GetString().Should().Be("lb");
    }

    [Fact]
    public async Task Reads_through_the_rule_authoring_resource_type_prefix()
    {
        // SourceField is persisted as "Observation.status", while the JSON has no such property. Getting this
        // wrong doesn't fail loudly — every rule silently resolves to null and nothing is ever transformed.
        var sut = CreateSut(Rule(
            TransformNodeType.StringNormalization,
            "Observation.status",
            new { @case = "upper" }));

        var result = await RunAsync(sut);

        JsonDocument.Parse(result.Json).RootElement.GetProperty("status").GetString().Should().Be("FINAL");
    }

    [Fact]
    public async Task An_explicit_write_back_path_redirects_the_output_away_from_the_read_path()
    {
        // The structure-building case: a rule reads a leaf but its output belongs at a different path.
        var sut = CreateSut(Rule(
            TransformNodeType.StringNormalization,
            "Observation.status",
            new { @case = "upper" },
            writeBackPath: "code.text"));

        var result = await RunAsync(sut);

        var root = JsonDocument.Parse(result.Json).RootElement;
        root.GetProperty("code").GetProperty("text").GetString().Should().Be("FINAL");
        root.GetProperty("status").GetString().Should().Be("final", "the read path must be left alone");
    }

    [Fact]
    public async Task Chains_multiple_rules_on_one_path_in_order()
    {
        var sut = CreateSut(
            Rule(TransformNodeType.RoundingScaling, "Observation.valueQuantity.value", new { decimalPlaces = "4" }, order: 0),
            Rule(TransformNodeType.RoundingScaling, "Observation.valueQuantity.value", new { decimalPlaces = "1" }, order: 1));

        var result = await RunAsync(sut);

        JsonDocument.Parse(result.Json).RootElement
            .GetProperty("valueQuantity").GetProperty("value").GetDouble().Should().Be(70.3);

        result.Hops.Should().HaveCount(2);
        result.Hops.Select(hop => hop.NodeOrder).Should().Equal(0, 1);
    }

    [Fact]
    public async Task A_path_that_does_not_exist_leaves_the_resource_untouched()
    {
        var sut = CreateSut(Rule(
            TransformNodeType.RoundingScaling,
            "Observation.valueQuantity.absent",
            new { decimalPlaces = "2" }));

        var result = await RunAsync(sut);

        result.Json.Should().Be(ObservationJson, "a missing value is not a reason to rewrite the document");
    }

    [Fact]
    public async Task No_matching_rules_returns_the_original_json_reference_unchanged()
    {
        var sut = CreateSut();

        var result = await RunAsync(sut);

        result.Json.Should().BeSameAs(ObservationJson);
        result.Hops.Should().BeEmpty();
    }

    [Fact]
    public async Task A_rule_that_produces_nothing_never_writes_a_null_into_the_resource()
    {
        // RoundingScaling returns Ok(null) for a value it can't parse as a number — a SUCCESS carrying no
        // value, not a failure. Patching that through would put an explicit JSON null into a FHIR element,
        // which a strict server (Medplum, validation-on Aidbox) rejects outright.
        var sut = CreateSut(Rule(
            TransformNodeType.RoundingScaling,
            "Observation.status",
            new { decimalPlaces = "2" }));

        var result = await RunAsync(sut);

        var status = JsonDocument.Parse(result.Json).RootElement.GetProperty("status");
        status.ValueKind.Should().Be(JsonValueKind.String);
        status.GetString().Should().Be("final");
    }

    [Fact]
    public async Task Hops_record_the_before_and_after_value_for_lineage()
    {
        var sut = CreateSut(Rule(
            TransformNodeType.RoundingScaling,
            "Observation.valueQuantity.value",
            new { decimalPlaces = "2" }));

        var hop = (await RunAsync(sut)).Hops.Should().ContainSingle().Subject;

        hop.Success.Should().BeTrue();
        hop.SourceField.Should().Be("Observation.valueQuantity.value");
        hop.WriteBackPath.Should().Be("valueQuantity.value");
        hop.Before.Should().Contain("70.30681818");
        hop.After.Should().Contain("70.31");
    }

    [Theory]
    [InlineData("Observation.status", "status")]
    [InlineData("status", "status")]
    [InlineData("Observation.code.coding.code", "code.coding.code")]
    [InlineData("ObservationDefinition.status", "ObservationDefinition.status")]
    public void StripResourceTypePrefix_only_removes_the_exact_resource_type_segment(string sourceField, string expected)
        => FhirResourceTransformService.StripResourceTypePrefix(sourceField, "Observation").Should().Be(expected);
}
