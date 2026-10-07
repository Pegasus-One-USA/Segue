using System.Text.Json;
using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services;
using FHIRBridge.Application.Services.Transforms;
using FHIRBridge.Application.Services.Transforms.Nodes;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Governance;
using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Application.Workflows.Catalog;
using FHIRBridge.Runtime.Application.Workflows.Payloads;
using FHIRBridge.Runtime.Domain.Workflows;
using FHIRBridge.Runtime.Infrastructure.Workflows.Executors;
using FluentAssertions;
using Moq;

namespace FHIRBridge.UnitTests.Workflows;

/// <summary>
/// The Transformations tab and the De-identification tab stay separate, but a column with its own Transformations
/// now gets them FIRST and its De-identification rule on their result. The workflow that surfaced this (ECW Backend
/// – Single Patient): PatientName ← Patient.name.given with Concatenation ("{0} {1}"), and a De-identification rule
/// masking $.name[*].given[*] (keepLength 4). Given ["DB","Tester"] it wrote "DB, **ster"; it must write
/// "DB Tester" → "*****ster".
///
/// Runs the REAL De-identification node (with the real Safe Harbor service and the rule above) straight into the
/// REAL Mapping node and mapping engine — only the rule repository, destination lookup and rule resolver are fakes.
/// </summary>
public sealed class TransformBeforeDeIdentificationTests
{
    private const string PatientJson = """
        {"resourceType":"Patient","id":"p1","name":[{"use":"official","family":"Sable","given":["DB","Tester"]}]}
        """;

    private const string MaskConfig = """{"mode":"mask","keepLength":"4"}""";

    private static readonly Guid ProfileId = Guid.NewGuid();

    private static readonly TransformationRule GivenMaskRule = new(
        TransformScope.ResourceType, TransformNodeType.HashingMasking, MaskConfig,
        resourceType: "Patient", sourceField: "$.name[*].given[*]",
        executionPhase: TransformExecutionPhase.PreMapping, deIdentificationProfileId: ProfileId);

    private static readonly TransformationRule ConcatRule = new(
        TransformScope.Field, TransformNodeType.ConcatenationTemplating,
        """{"mode":"concat","separator":"","template":"{0} {1}"}""",
        resourceType: "Patient", destinationField: "PatientName");

    private static SafeHarborDeIdentificationService DeIdentificationService(params TransformationRule[] preMappingRules)
    {
        var repository = new Mock<ITransformationRuleRepository>();
        repository
            .Setup(r => r.GetPreMappingRulesAsync(ProfileId, "Patient", It.IsAny<CancellationToken>()))
            .ReturnsAsync(preMappingRules);
        return new SafeHarborDeIdentificationService(repository.Object);
    }

    private static MappingFieldDto Column(string target, string jsonPath, ArrayPolicy policy = ArrayPolicy.FirstItem) => new(
        TargetField: target, JsonPath: jsonPath, ValueType: MappingValueType.String, IsRequired: false,
        DefaultValue: null, Format: null, ResourceType: "Patient", DestinationObject: "dbo.Patient",
        ArrayPolicy: policy, ArrayAncestors: ["name"]);

    private static WorkflowNode Node(string nodeType, WorkflowNodeCategory category, object config) =>
        new WorkflowDefinition(Guid.NewGuid(), "transform-before-deid", 1)
            .AddNode(nodeType, category, 50, configurationJson: JsonSerializer.Serialize(config));

    /// <summary>Source → De-identification → Mapping, returning the De-identification output and the mapped record.</summary>
    private static async Task<(WorkflowNodeOutput DeIdentified, MappedDestinationRecord Record)> RunAsync(
        SafeHarborDeIdentificationService deIdentification,
        IReadOnlyDictionary<string, IReadOnlyList<TransformationRule>> chains,
        params MappingFieldDto[] columns)
    {
        var context = new WorkflowExecutionContext(Guid.NewGuid(), "test");
        var source = new WorkflowNodeOutput(
            Guid.NewGuid(), WorkflowNodeTypes.EpicSource,
            new ResourceBatch([new ResourceEnvelope("Patient", "p1", PatientJson)]), WorkflowDataContract.ResourceBatch);

        var deIdentified = await new DeIdentificationNodeExecutor(deIdentification).ExecuteAsync(
            context,
            Node(WorkflowNodeTypes.DeIdentification, WorkflowNodeCategory.Compliance, new { profileId = ProfileId.ToString() }),
            [source],
            CancellationToken.None);

        var destination = new DestinationConfiguration(
            "Test SQL", DestinationType.SqlServer, new SecretReference("kv", "secret"), "FHIRBridge");
        var configuration = new Mock<IConfigurationRepository>();
        configuration.Setup(r => r.GetDestinationAsync(destination.Id, It.IsAny<CancellationToken>())).ReturnsAsync(destination);
        var resolver = new Mock<IEffectiveRuleResolver>();
        resolver
            .Setup(r => r.ResolveAsync(
                It.IsAny<DestinationType>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<string?>()))
            .ReturnsAsync((DestinationType _, string _, string field, Guid? _, Guid? _, string? _, string? _, CancellationToken _, bool _, bool _, string? _)
                => chains.GetValueOrDefault(field) ?? []);

        var mapping = new MappingNodeExecutor(
            new JsonMappingEngine(), mappingMaterializer: null, configurationRepository: configuration.Object,
            ruleResolver: resolver.Object,
            transformNodeRegistry: new TransformNodeRegistry([new ConcatenationTemplatingNode(), new HashingMaskingNode()]),
            deIdentificationService: deIdentification);
        var mappingNode = Node(WorkflowNodeTypes.Mapping, WorkflowNodeCategory.Transform, new Dictionary<string, object>
        {
            ["resourceType"] = "Patient",
            ["destinationObject"] = "dbo.Patient",
            ["fields"] = columns,
            ["destinationId"] = destination.Id.ToString(),
        });

        var output = await mapping.ExecuteAsync(context, mappingNode, [deIdentified], CancellationToken.None);
        return (deIdentified, (MappedDestinationRecord)((MappedRecordBatch)output.Payload!).Records.Single());
    }

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<TransformationRule>> PatientNameConcat =
        new Dictionary<string, IReadOnlyList<TransformationRule>> { ["PatientName"] = [ConcatRule] };

    [Fact]
    public async Task Concatenation_runs_first_then_the_De_identification_rule_on_its_result()
    {
        var (_, record) = await RunAsync(
            DeIdentificationService(GivenMaskRule), PatientNameConcat, Column("PatientName", "$.name[*].given"));

        record.Values["PatientName"].Should().Be("*****ster");   // "DB Tester", masked with keepLength 4
    }

    [Fact]
    public async Task The_same_rule_on_the_joined_value_matches_what_the_De_identification_service_itself_produces()
    {
        // The mask is the De-identification tab's own rule, not the Hashing/Masking transform node — prove the
        // expected value comes from that rule rather than a hardcoded string.
        var service = DeIdentificationService(GivenMaskRule);
        var expected = service.DeIdentifyValue(
            "DB Tester", new DeIdentificationFieldHop("$.name[*].given[*]", "Mask", MaskConfig, null, null, true, null));

        var (_, record) = await RunAsync(service, PatientNameConcat, Column("PatientName", "$.name[*].given"));

        record.Values["PatientName"].Should().Be(expected);
    }

    [Fact]
    public async Task A_column_without_Transformations_is_still_redacted_before_mapping_exactly_as_before()
    {
        var (_, record) = await RunAsync(
            DeIdentificationService(GivenMaskRule), PatientNameConcat,
            Column("PatientName", "$.name[*].given"),
            Column("GivenNames", "$.name[*].given"));

        record.Values["GivenNames"].Should().Be("DB, **ster");
    }

    [Fact]
    public async Task No_unredacted_name_reaches_the_destination_record_or_the_resource_passed_downstream()
    {
        var (deIdentified, record) = await RunAsync(
            DeIdentificationService(GivenMaskRule), PatientNameConcat,
            Column("PatientName", "$.name[*].given"),
            Column("GivenNames", "$.name[*].given"));

        JsonSerializer.Serialize(record.Values).Should().NotContain("Tester");
        record.SourceJson.Should().NotContain("Tester");
        var envelope = (ResourceEnvelope)((DeIdentifiedBatch)deIdentified.Payload!).Records.Single();
        ((string)envelope.Payload).Should().NotContain("Tester");
    }

    [Fact]
    public async Task The_unredacted_copy_is_never_serialized()
    {
        var (deIdentified, _) = await RunAsync(
            DeIdentificationService(GivenMaskRule), PatientNameConcat, Column("PatientName", "$.name[*].given"));
        var envelope = (ResourceEnvelope)((DeIdentifiedBatch)deIdentified.Payload!).Records.Single();

        envelope.PreDeIdentificationPayload.Should().Contain("Tester");   // held in memory for the Mapping node...
        JsonSerializer.Serialize(envelope).Should().NotContain("Tester"); // ...but never written anywhere
        JsonSerializer.Serialize(deIdentified.Payload).Should().NotContain("Tester");
    }

    [Fact]
    public async Task Nothing_redacted_means_no_unredacted_copy_and_the_chain_joins_the_real_names()
    {
        var (deIdentified, record) = await RunAsync(
            DeIdentificationService(), PatientNameConcat, Column("PatientName", "$.name[*].given"));

        ((ResourceEnvelope)((DeIdentifiedBatch)deIdentified.Payload!).Records.Single()).PreDeIdentificationPayload.Should().BeNull();
        record.Values["PatientName"].Should().Be("DB Tester");   // the parts, not the ", "-joined "DB, Tester"
    }

    [Fact]
    public async Task A_rule_on_a_different_element_is_not_moved_after_this_columns_Transformations()
    {
        var familyMask = new TransformationRule(
            TransformScope.ResourceType, TransformNodeType.HashingMasking, MaskConfig,
            resourceType: "Patient", sourceField: "$.name[*].family",
            executionPhase: TransformExecutionPhase.PreMapping, deIdentificationProfileId: ProfileId);

        var (_, record) = await RunAsync(
            DeIdentificationService(familyMask), PatientNameConcat, Column("PatientName", "$.name[*].given"));

        record.Values["PatientName"].Should().Be("DB Tester");   // given is not covered by any rule
    }
}

/// <summary>A path that stops ON an array of plain values ("$.name[*].given") hands a transform chain the array's
/// items as parts — the same parts "...given[*]" yields — while the column's own value is unchanged.</summary>
public sealed class JsonMappingEngineArrayItemPartsTests
{
    private const string TwoNames = """
        {"resourceType":"Patient","name":[{"given":["DB","Tester"]},{"given":["Dee","Bee"]}],
         "address":[{"line":["1 Main St","Apt 2"]}],"gender":"male"}
        """;

    private static MappingTestResultDto Map(string target, string path, ArrayPolicy policy, string[] ancestors, string? format = null) =>
        new JsonMappingEngine().Map(TwoNames,
        [
            new MappingFieldDto(target, path, MappingValueType.String, false, null, format, "Patient", "dbo.Patient",
                ArrayPolicy: policy, ArrayAncestors: ancestors),
        ]);

    [Fact]
    public void First_instance_hands_that_names_given_items()
    {
        var result = Map("PatientName", "$.name[*].given", ArrayPolicy.FirstItem, ["name"]);

        result.RawArrayValues!["PatientName"].Should().Equal("DB", "Tester");
    }

    [Fact]
    public void The_column_value_itself_is_unchanged()
    {
        var before = Map("PatientName", "$.name[*].given[*]", ArrayPolicy.FirstItem, ["name"]);
        var arrayPath = Map("PatientName", "$.name[*].given", ArrayPolicy.FirstItem, ["name"]);

        arrayPath.RawArrayValues!["PatientName"].Should().Equal(before.RawArrayValues!["PatientName"]);
        arrayPath.Values["PatientName"].Should().Be("DB, Tester");
    }

    [Fact]
    public void Nth_instance_hands_that_instances_items()
    {
        // index=N is zero-based: index=1 is the second name. Same narrowing as the "...given[*]" path gets.
        var arrayPath = Map("PatientName", "$.name[*].given", ArrayPolicy.FirstItem, ["name"], format: "index=1");
        var itemPath = Map("PatientName", "$.name[*].given[*]", ArrayPolicy.FirstItem, ["name"], format: "index=1");

        arrayPath.RawArrayValues!["PatientName"].Should().Equal("Dee", "Bee");
        arrayPath.RawArrayValues!["PatientName"].Should().Equal(itemPath.RawArrayValues!["PatientName"]);
    }

    [Fact]
    public void All_records_hands_every_item()
    {
        var result = Map("PatientName", "$.name[*].given", ArrayPolicy.RepeatParent, ["name"]);

        result.RawArrayValues!["PatientName"].Should().Equal("DB", "Tester", "Dee", "Bee");
    }

    [Fact]
    public void Address_lines_work_the_same_way()
    {
        var result = Map("Street", "$.address[*].line", ArrayPolicy.FirstItem, ["address"]);

        result.RawArrayValues!["Street"].Should().Equal("1 Main St", "Apt 2");
    }

    [Fact]
    public void A_plain_value_or_an_array_of_objects_is_left_as_it_was()
    {
        Map("Gender", "$.gender", ArrayPolicy.Scalar, []).RawArrayValues!["Gender"].Should().Equal("male");
        Map("Names", "$.name", ArrayPolicy.FirstItem, []).RawArrayValues!["Names"].Should().HaveCount(1);
    }
}
