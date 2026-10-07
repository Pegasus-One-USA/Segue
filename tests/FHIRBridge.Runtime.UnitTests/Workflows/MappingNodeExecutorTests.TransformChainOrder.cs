using System.Text.Json;
using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services.Transforms;
using FHIRBridge.Application.Services.Transforms.Nodes;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Runtime.Application.Workflows.Payloads;
using FHIRBridge.Runtime.Infrastructure.Workflows.Executors;
using FluentAssertions;
using Moq;
using ResourceEnvelope = FHIRBridge.Runtime.Application.Workflows.Payloads.ResourceEnvelope;

namespace FHIRBridge.Runtime.UnitTests.Workflows;

/// <summary>
/// A column's transformation chain runs in its configured order, each step receiving the previous step's output:
/// Input → A → output(A) → B → output(B) → … The scenario that surfaced this is a Patient's given names
/// ["DB", "Tester"] mapped to PatientName with Concatenation (" ") and masking (Hashing/Masking, the field-level
/// de-identification step). Expected values are computed by the real nodes from the rule's own config rather than
/// hardcoded, so they follow whatever the configured masking rule does.
///
/// The reverse order (Masking → Concatenation) used to lose every repeat but one: the chain was handed the whole
/// array only when its FIRST step was a join, so the later Concatenation got one collapsed name and joined nothing.
/// </summary>
public sealed partial class MappingNodeExecutorTests
{
    private static readonly string[] GivenNames = ["DB", "Tester"];

    private static TransformationRule ChainRule(TransformNodeType nodeType, Dictionary<string, string> config, int order, string field = "PatientName", string resourceType = "Patient") =>
        new(TransformScope.Field, nodeType, JsonSerializer.Serialize(config), resourceType: resourceType, destinationField: field, order: order);

    private static Dictionary<string, string> Concat(string separator) => new() { ["mode"] = "concat", ["separator"] = separator };
    private static Dictionary<string, string> Mask(int keepLength) => new() { ["mode"] = "mask", ["keepLength"] = keepLength.ToString() };
    private static Dictionary<string, string> Upper() => new() { ["case"] = "upper" };

    /// <summary>What the real node does to one value — the oracle for expected results.</summary>
    private static object? Apply(ITransformNode node, object? value, Dictionary<string, string> config) =>
        node.Execute(value, config, secret: null).Value;

    /// <summary>Records every input a node receives, so a test can prove what each step was actually handed.</summary>
    private sealed class RecordingNode(ITransformNode inner) : ITransformNode
    {
        public List<object?> Inputs { get; } = [];
        public TransformNodeType NodeType => inner.NodeType;
        public bool AcceptsCollections => inner.AcceptsCollections;
        public bool AcceptsStructuredValue => inner.AcceptsStructuredValue;

        public TransformResult Execute(object? value, IReadOnlyDictionary<string, string> config, string? secret)
        {
            Inputs.Add(value);
            return inner.Execute(value, config, secret);
        }
    }

    /// <summary>Runs one mapped column through the real MappingNodeExecutor with the given chain.
    /// <paramref name="collapsedValue"/> is the single value Instance Selection picked; <paramref name="repeats"/>
    /// is every occurrence of the repeating source field (null for a non-repeating, scalar field).</summary>
    private static async Task<object?> RunChainAsync(
        TransformationRule[] chain, object? collapsedValue, IReadOnlyList<object?>? repeats, params ITransformNode[] nodes)
    {
        var resourceType = chain[0].ResourceType!;
        var field = chain[0].DestinationField!;
        var destination = new DestinationConfiguration(
            "Test SQL", DestinationType.SqlServer, new SecretReference("kv", "secret"), "FHIRBridge");

        var fields = new[]
        {
            new MappingFieldDto(field, "$.name[*].given", MappingValueType.String, IsRequired: false, DefaultValue: null,
                Format: "directField", ResourceType: resourceType, DestinationObject: resourceType),
        };
        var engine = new FakeJsonMappingEngine(new MappingTestResultDto(
            Values: new Dictionary<string, object?> { [field] = collapsedValue },
            Errors: [],
            RawArrayValues: repeats is null ? null : new Dictionary<string, IReadOnlyList<object?>> { [field] = repeats }));

        var repository = new Mock<IConfigurationRepository>();
        repository.Setup(r => r.GetDestinationAsync(destination.Id, It.IsAny<CancellationToken>())).ReturnsAsync(destination);
        // The resolver hands rules back in their stored Order (EfTransformationRuleRepository orders by it).
        var resolver = new Mock<IEffectiveRuleResolver>();
        resolver
            .Setup(r => r.ResolveAsync(
                It.IsAny<DestinationType>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<string?>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)chain.OrderBy(r => r.Order).ToArray());
        var settingsCache = new Mock<ISystemSettingsCache>();
        settingsCache
            .Setup(c => c.GetBoolAsync(TransformationRulesFeatureFlag.SettingKey, TransformationRulesFeatureFlag.DefaultHidden, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var executor = new MappingNodeExecutor(
            engine, mappingMaterializer: null, configurationRepository: repository.Object,
            ruleResolver: resolver.Object, transformNodeRegistry: new TransformNodeRegistry(nodes), settingsCache: settingsCache.Object);
        var node = CreateNode(resourceType, resourceType, fields, extraConfig: new Dictionary<string, object>
        {
            ["destinationId"] = destination.Id.ToString(),
        });
        var upstream = UpstreamWith(new ResourceEnvelope(resourceType, "r1", $$"""{"resourceType":"{{resourceType}}"}"""));

        var output = await executor.ExecuteAsync(CreateContext(), node, [upstream], CancellationToken.None);

        return ((MappedDestinationRecord)((MappedRecordBatch)output.Payload!).Records.Single()).Values[field];
    }

    [Fact]
    public async Task Single_transformation_concatenation_joins_every_given_name()
    {
        var value = await RunChainAsync(
            [ChainRule(TransformNodeType.ConcatenationTemplating, Concat(" "), order: 0)],
            collapsedValue: "DB", repeats: GivenNames, new ConcatenationTemplatingNode());

        value.Should().Be("DB Tester");
    }

    [Fact]
    public async Task Concatenation_then_masking_masks_the_joined_value_DB_Tester()
    {
        var concat = new RecordingNode(new ConcatenationTemplatingNode());
        var mask = new RecordingNode(new HashingMaskingNode());

        var value = await RunChainAsync(
            [
                ChainRule(TransformNodeType.ConcatenationTemplating, Concat(" "), order: 0),
                ChainRule(TransformNodeType.HashingMasking, Mask(4), order: 1),
            ],
            collapsedValue: "DB", repeats: GivenNames, concat, mask);

        concat.Inputs.Should().ContainSingle().Which.Should().BeEquivalentTo(GivenNames);
        mask.Inputs.Should().Equal(["DB Tester"]);   // De-identification receives Concatenation's output.
        value.Should().Be(Apply(new HashingMaskingNode(), "DB Tester", Mask(4)));
        value.Should().Be("*****ster");                // mask, keepLength 4, on the 9-character "DB Tester".
    }

    [Fact]
    public async Task Masking_then_concatenation_masks_each_name_then_joins_them_without_dropping_any()
    {
        var mask = new RecordingNode(new HashingMaskingNode());

        var value = await RunChainAsync(
            [
                ChainRule(TransformNodeType.HashingMasking, Mask(4), order: 0),
                ChainRule(TransformNodeType.ConcatenationTemplating, Concat(" "), order: 1),
            ],
            collapsedValue: "DB", repeats: GivenNames, mask, new ConcatenationTemplatingNode());

        mask.Inputs.Should().Equal(["DB", "Tester"]);  // once per name — never once over the list, never only "DB"
        var maskedNames = GivenNames.Select(n => Apply(new HashingMaskingNode(), n, Mask(4))).ToArray();
        value.Should().Be(Apply(new ConcatenationTemplatingNode(), maskedNames, Concat(" ")));
        value.Should().Be("DB **ster");                // "DB" is within keepLength 4, so the rule leaves it as-is.
    }

    [Fact]
    public async Task Configured_order_wins_over_list_position()
    {
        // Same two rules handed over in the opposite list order: Order decides, so Concatenation still runs first.
        var value = await RunChainAsync(
            [
                ChainRule(TransformNodeType.HashingMasking, Mask(4), order: 1),
                ChainRule(TransformNodeType.ConcatenationTemplating, Concat(" "), order: 0),
            ],
            collapsedValue: "DB", repeats: GivenNames, new ConcatenationTemplatingNode(), new HashingMaskingNode());

        value.Should().Be("*****ster");
    }

    [Fact]
    public async Task Three_step_chain_runs_A_then_B_then_C_each_on_the_previous_output()
    {
        var upper = new RecordingNode(new StringNormalizationNode());
        var concat = new RecordingNode(new ConcatenationTemplatingNode());
        var mask = new RecordingNode(new HashingMaskingNode());

        var value = await RunChainAsync(
            [
                ChainRule(TransformNodeType.StringNormalization, Upper(), order: 0),
                ChainRule(TransformNodeType.ConcatenationTemplating, Concat(" "), order: 1),
                ChainRule(TransformNodeType.HashingMasking, Mask(4), order: 2),
            ],
            collapsedValue: "DB", repeats: GivenNames, upper, concat, mask);

        upper.Inputs.Should().Equal(["DB", "Tester"]);
        concat.Inputs.Should().ContainSingle().Which.Should().BeEquivalentTo(new[] { "DB", "TESTER" });
        mask.Inputs.Should().Equal(["DB TESTER"]);
        value.Should().Be("*****STER");
    }

    [Fact]
    public async Task Scalar_input_runs_the_same_chain_on_the_single_value()
    {
        var value = await RunChainAsync(
            [
                ChainRule(TransformNodeType.ConcatenationTemplating, Concat(" "), order: 0),
                ChainRule(TransformNodeType.HashingMasking, Mask(4), order: 1),
            ],
            collapsedValue: "Tester", repeats: null, new ConcatenationTemplatingNode(), new HashingMaskingNode());

        value.Should().Be("**ster");
    }

    [Fact]
    public async Task A_chain_with_no_join_step_still_receives_the_collapsed_value_as_before()
    {
        var mask = new RecordingNode(new HashingMaskingNode());

        var value = await RunChainAsync(
            [ChainRule(TransformNodeType.HashingMasking, Mask(4), order: 0)],
            collapsedValue: "Tester", repeats: GivenNames, mask);

        mask.Inputs.Should().Equal(["Tester"]);
        value.Should().Be("**ster");
    }

    [Fact]
    public async Task Observation_resource_chains_the_same_way()
    {
        var value = await RunChainAsync(
            [
                ChainRule(TransformNodeType.StringNormalization, Upper(), order: 0, field: "CodeDisplay", resourceType: "Observation"),
                ChainRule(TransformNodeType.ConcatenationTemplating, Concat("; "), order: 1, field: "CodeDisplay", resourceType: "Observation"),
            ],
            collapsedValue: "Body temperature", repeats: ["Body temperature", "Temp"],
            new StringNormalizationNode(), new ConcatenationTemplatingNode());

        value.Should().Be("BODY TEMPERATURE; TEMP");
    }
}
