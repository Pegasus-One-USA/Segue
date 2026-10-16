using System.Text.Json;
using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Application.Abstractions.Mapping;
using FHIRBridge.Application.Abstractions.Messaging;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services;
using FHIRBridge.Application.Services.Transforms;
using FHIRBridge.Application.Services.Transforms.Nodes;
using FHIRBridge.Application.Messaging;
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
///
/// In the "ErrorCaptureRelay" collection: the real Mapping node writes WorkflowDebug trace lines, which are
/// process-wide, so running alongside the relay tests (ResourceIdEndToEndTests and co.) would feed them lines
/// they did not produce.
/// </summary>
[Collection("ErrorCaptureRelay")]
public sealed class TransformBeforeDeIdentificationTests
{
    private const string PatientJson = """
        {"resourceType":"Patient","id":"p1","birthDate":"1980-03-15","name":[{"use":"official","family":"Sable","given":["DB","Tester"]}]}
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
        // No rules for any other resource type (a reference's target type is looked up when rewriting references).
        repository
            .Setup(r => r.GetPreMappingRulesAsync(ProfileId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        repository
            .Setup(r => r.GetPreMappingRulesAsync(ProfileId, "Patient", It.IsAny<CancellationToken>()))
            .ReturnsAsync(preMappingRules);
        repository
            .Setup(r => r.HasPreMappingRulesAsync(ProfileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(preMappingRules.Length > 0);
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
    /// <summary>Every lineage hop the Mapping node dispatched during the last RunAsync.</summary>
    private readonly List<LineageHopEntryDto> _lineage = [];

    /// <summary>When set, applied to each envelope between the De-identification and Mapping nodes.</summary>
    private Func<ResourceEnvelope, ResourceEnvelope>? _between;

    /// <summary>When set, the de-identification service the MAPPING node is given (the De-identification node keeps
    /// the one RunAsync is called with) — stands in for a host that registers a different one.</summary>
    private IDeIdentificationService? _mappingDeIdentification;

    /// <summary>When set, the mapping engine the Mapping node uses instead of the real one.</summary>
    private IJsonMappingEngine? _mappingEngine;

    /// <summary>Transform nodes registered in place of the standard ones of the same type.</summary>
    private ITransformNode[] _nodeOverrides = [];

    /// <summary>Every log line the Mapping node wrote during the last RunAsync, formatted.</summary>
    private readonly List<string> _logs = [];

    /// <summary>The Mapping node's output (its metadata carries the run-level omissions) from the last RunAsync.</summary>
    private WorkflowNodeOutput? _mappingOutput;

    /// <summary>The Patient resource RunAsync sends through the pipeline.</summary>
    private string _patientJson = PatientJson;

    private async Task<(WorkflowNodeOutput DeIdentified, MappedDestinationRecord Record)> RunAsync(
        SafeHarborDeIdentificationService deIdentification,
        IReadOnlyDictionary<string, IReadOnlyList<TransformationRule>> chains,
        params MappingFieldDto[] columns)
    {
        var context = new WorkflowExecutionContext(Guid.NewGuid(), "test");
        var source = new WorkflowNodeOutput(
            Guid.NewGuid(), WorkflowNodeTypes.EpicSource,
            new ResourceBatch([new ResourceEnvelope("Patient", "p1", _patientJson)]), WorkflowDataContract.ResourceBatch);

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

        ITransformNode[] standardNodes =
        [
            new ConcatenationTemplatingNode(), new HashingMaskingNode(), new StringNormalizationNode(),
            new DefaultNullHandlingNode(), new ArrayListOperationsNode(),
        ];
        var mapping = new MappingNodeExecutor(
            _mappingEngine ?? new JsonMappingEngine(), mappingMaterializer: null, configurationRepository: configuration.Object,
            ruleResolver: resolver.Object,
            transformNodeRegistry: new TransformNodeRegistry(
                standardNodes.Where(node => _nodeOverrides.All(o => o.NodeType != node.NodeType)).Concat(_nodeOverrides)),
            lineageCaptureDispatcher: new CollectingLineageDispatcher(_lineage),
            loggerFactory: new CapturingLoggerFactory(_logs),
            deIdentificationService: _mappingDeIdentification ?? deIdentification);
        var mappingNode = Node(WorkflowNodeTypes.Mapping, WorkflowNodeCategory.Transform, new Dictionary<string, object>
        {
            ["resourceType"] = "Patient",
            ["destinationObject"] = "dbo.Patient",
            ["fields"] = columns,
            ["destinationId"] = destination.Id.ToString(),
        });

        // Optionally stands in for a node between De-identification and Mapping that rewrites each envelope.
        var mappingInput = _between is null
            ? deIdentified
            : new WorkflowNodeOutput(
                deIdentified.NodeId, deIdentified.NodeType,
                new DeIdentifiedBatch(((DeIdentifiedBatch)deIdentified.Payload!).Records
                    .Select(record => (object)_between((ResourceEnvelope)record)).ToArray()),
                deIdentified.Contract, deIdentified.Metadata);
        var output = await mapping.ExecuteAsync(context, mappingNode, [mappingInput], CancellationToken.None);
        _mappingOutput = output;
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

        DeIdentificationSnapshots.DeferrableOriginalIfCurrent(envelope).Should().Contain("Tester");   // in memory for Mapping...
        JsonSerializer.Serialize(envelope).Should().NotContain("Tester");   // ...but unreachable by any serializer,
        JsonSerializer.Serialize(deIdentified.Payload).Should().NotContain("Tester");
        envelope.ToString().Should().NotContain("Tester");                  // by ToString,
        envelope.Should().Be(envelope with { });                            // and by equality.
    }

    [Fact]
    public async Task Nothing_redacted_means_no_unredacted_copy_and_the_chain_joins_the_real_names()
    {
        // The policy has a rule, but it matches nothing this resource carries: a valid policy that redacts nothing
        // here (an EMPTY policy is a configuration error that fails the run — see the fail-closed tests).
        var (deIdentified, record) = await RunAsync(
            DeIdentificationService(PreMapping("$.telecom[*].value", MaskConfig)), PatientNameConcat,
            Column("PatientName", "$.name[*].given"));

        DeIdentificationSnapshots.DeferrableOriginalIfCurrent((ResourceEnvelope)((DeIdentifiedBatch)deIdentified.Payload!).Records.Single()).Should().BeNull();
        // Not deferred, so the long-standing contract holds: the array path hands the chain its one joined value.
        record.Values["PatientName"].Should().Be(PreContractConcat("DB, Tester"));
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

        // given is not covered by any rule: not deferred, so the long-standing contract (one joined value) holds.
        record.Values["PatientName"].Should().Be(PreContractConcat("DB, Tester"));
    }

    // ── Joined columns ("a|b"): deferred only when EVERY joined path is covered ──────────────────────────────

    private static readonly TransformationRule FamilyMaskRule = new(
        TransformScope.ResourceType, TransformNodeType.HashingMasking, MaskConfig,
        resourceType: "Patient", sourceField: "$.name[*].family",
        executionPhase: TransformExecutionPhase.PreMapping, deIdentificationProfileId: ProfileId);

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<TransformationRule>> FullNameUpper =
        new Dictionary<string, IReadOnlyList<TransformationRule>>
        {
            ["FullName"] =
            [
                new TransformationRule(TransformScope.Field, TransformNodeType.StringNormalization,
                    """{"case":"upper"}""", resourceType: "Patient", destinationField: "FullName"),
            ],
        };

    private static MappingFieldDto JoinedFullName() => Column("FullName", "$.name[*].given[*]|$.name[*].family") with
    {
        Format = "joinedFields;delimiter= ",
    };

    [Fact]
    public async Task A_joined_column_covered_only_in_part_keeps_redaction_first_and_never_masks_the_uncovered_part()
    {
        var (_, record) = await RunAsync(DeIdentificationService(GivenMaskRule), FullNameUpper, JoinedFullName());

        var value = (string)record.Values["FullName"]!;
        value.Should().NotContain("TESTER");   // the covered part is still redacted (before the join)
        value.Should().Contain("SABLE");       // the uncovered part is not swept up in the given rule's mask
    }

    [Fact]
    public async Task A_joined_column_covered_in_full_runs_its_Transformations_first_then_both_redactions()
    {
        var (_, record) = await RunAsync(DeIdentificationService(GivenMaskRule, FamilyMaskRule), FullNameUpper, JoinedFullName());

        var value = (string)record.Values["FullName"]!;
        value.Should().NotContain("TESTER").And.NotContain("SABLE");
        value.Should().EndWith("ABLE");        // masked as ONE value, keepLength 4
    }

    // ── DeIdentifyValue fails closed ──────────────────────────────────────────────────────────────────────

    private static DeIdentificationFieldHop Hop(string configJson = MaskConfig) =>
        new("$.name[*].given[*]", "Mask", configJson, null, null, true, null);

    [Fact]
    public void DeIdentifyValue_redacts_a_string_and_each_string_of_a_list()
    {
        var service = DeIdentificationService();

        service.DeIdentifyValue("DB Tester", Hop()).Should().Be("*****ster");
        service.DeIdentifyValue(new List<string?> { "Tester", null }, Hop())
            .Should().BeEquivalentTo(new[] { "**ster", null });
    }

    [Fact]
    public void DeIdentifyValue_returns_null_when_it_cannot_redact_with_certainty()
    {
        var service = DeIdentificationService();

        service.DeIdentifyValue("DB Tester", Hop("""{"mode":"no-such-strategy"}""")).Should().BeNull();
        service.DeIdentifyValue("DB Tester", Hop("not json")).Should().BeNull();
        service.DeIdentifyValue(System.Text.Json.Nodes.JsonNode.Parse("""{"given":"Tester"}"""), Hop()).Should().BeNull();
        service.DeIdentifyValue(new Dictionary<string, string> { ["given"] = "Tester" }, Hop()).Should().BeNull();
        service.DeIdentifyValue("Tester"u8.ToArray(), Hop()).Should().BeNull();
        service.DeIdentifyValue(42, Hop()).Should().BeNull();
    }

    // ── The unredacted copy never reaches ToString / equality ─────────────────────────────────────────────

    [Fact]
    public void An_envelopes_ToString_and_equality_leave_out_the_unredacted_copy()
    {
        var redacted = new ResourceEnvelope("Patient", "p1", """{"given":["DB","**ster"]}""");
        DeIdentificationSnapshots.Attach(redacted, PatientJson, (string)redacted.Payload);

        redacted.ToString().Should().NotContain("Tester").And.Contain("**ster").And.Contain("p1");
        var copy = redacted with { };
        copy.Should().Be(redacted);   // equality is untouched by the snapshot...
        DeIdentificationSnapshots.DeferrableOriginalIfCurrent(copy).Should().BeNull();   // ...which a new instance never carries
        DeIdentificationSnapshots.DeferrableOriginalIfCurrent(redacted).Should().Be(PatientJson);
    }

    // ── Deferral is allowed only where it is safe; everything else keeps redaction-first ─────────────────────

    private static TransformationRule PreMapping(string sourceField, string configJson) => new(
        TransformScope.ResourceType, TransformNodeType.HashingMasking, configJson,
        resourceType: "Patient", sourceField: sourceField,
        executionPhase: TransformExecutionPhase.PreMapping, deIdentificationProfileId: ProfileId);

    // ── Fourth review ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_snapshot_restores_only_what_a_deferrable_rule_covered()
    {
        // given is masked (deferrable); family is hashed (never deferred). The snapshot holds the real given names
        // the chain needs — and nothing else unredacted: the family name stays hashed in it.
        var (deIdentified, _) = await RunAsync(
            DeIdentificationService(GivenMaskRule, PreMapping("$.name[*].family", """{"mode":"hash"}""")),
            PatientNameConcat, Column("PatientName", "$.name[*].given"));
        var snapshot = DeIdentificationSnapshots.DeferrableOriginalIfCurrent(
            (ResourceEnvelope)((DeIdentifiedBatch)deIdentified.Payload!).Records.Single());

        snapshot.Should().Contain("Tester").And.NotContain("Sable");
    }

    [Fact]
    public async Task A_path_also_covered_by_a_non_deferrable_rule_is_not_restored_and_stays_redaction_first()
    {
        // Mask AND Hash on the same element: the Hash must keep running on the raw value, so the element is not
        // restored and the column is not deferred.
        var (deIdentified, record) = await RunAsync(
            DeIdentificationService(GivenMaskRule, PreMapping("$.name[*].given[*]", """{"mode":"hash"}""")),
            PatientNameConcat, Column("PatientName", "$.name[*].given"));
        var snapshot = DeIdentificationSnapshots.DeferrableOriginalIfCurrent(
            (ResourceEnvelope)((DeIdentifiedBatch)deIdentified.Payload!).Records.Single());

        (snapshot ?? string.Empty).Should().NotContain("Tester");
        ((string)record.Values["PatientName"]!).Should().NotContain("Tester");
        _lineage.Should().NotContain(hop => hop.NodeType == "DeIdentification:DeferralFallback");
    }

    [Fact]
    public async Task A_snapshot_that_cannot_be_read_falls_back_to_the_redacted_value_and_says_so()
    {
        _between = envelope =>
        {
            var replacement = envelope with { };
            DeIdentificationSnapshots.Attach(replacement, "not json at all", (string)envelope.Payload);
            return replacement;
        };

        var (_, record) = await RunAsync(DeIdentificationService(GivenMaskRule), PatientNameConcat, Column("PatientName", "$.name[*].given"));

        record.Values["PatientName"].Should().Be(PreContractConcat("DB, **ster"));   // redacted, never raw, never empty
        var fallback = _lineage.Single(hop => hop.NodeType == "DeIdentification:DeferralFallback");
        fallback.Success.Should().BeFalse();
        fallback.ErrorMessage.Should().Contain("failed").And.NotContain("Tester");
    }

    [Fact]
    public async Task A_column_with_no_source_value_is_null_and_is_not_reported_as_a_failure()
    {
        var chains = new Dictionary<string, IReadOnlyList<TransformationRule>>
        {
            ["PatientName"] = [ConcatRule],
            ["Suffix"] = [new TransformationRule(TransformScope.Field, TransformNodeType.StringNormalization,
                """{"trim":true}""", resourceType: "Patient", destinationField: "Suffix")],
        };

        var (_, record) = await RunAsync(
            DeIdentificationService(GivenMaskRule, PreMapping("$.name[*].suffix[*]", MaskConfig)), chains,
            Column("PatientName", "$.name[*].given"), Column("Suffix", "$.name[*].suffix"));

        record.Values["PatientName"].Should().Be("*****ster");
        record.Values.GetValueOrDefault("Suffix").Should().BeNull();   // missing in the source: null, not a failure
        _lineage.Should().NotContain(hop => hop.NodeType == "DeIdentification:DeferralFallback");
        _lineage.Where(hop => hop.DestinationField == "Suffix").Should().OnlyContain(hop => hop.Success);
    }

    [Fact]
    public async Task A_Remove_rule_after_the_chain_writes_nothing_not_a_list_of_nulls()
    {
        var (_, record) = await RunAsync(
            DeIdentificationService(PreMapping("$.name[*].given[*]", """{"mode":"remove"}""")), PatientNameConcat,
            Column("PatientName", "$.name[*].given"));

        record.Values.GetValueOrDefault("PatientName").Should().BeNull();
        JsonSerializer.Serialize(record.Values).Should().NotContain("Tester").And.NotContain("[null");
    }

    [Theory]
    [InlineData("scalar")]
    [InlineData("list")]
    [InlineData("mixed")]
    public void Remove_drops_the_whole_value_whatever_its_shape(string shape)
    {
        object value = shape switch
        {
            "scalar" => "DB Tester",
            "list" => new List<string?> { "DB", null, "Tester" },
            _ => new List<object?> { "DB", 7 },
        };

        DeIdentificationService().DeIdentifyValue(
                value, new DeIdentificationFieldHop("$.name[*].given[*]", "Remove", """{"mode":"remove"}""", null, null, true, null))
            .Should().BeNull();
    }

    [Fact]
    public void Mask_on_a_list_redacts_each_item_and_keeps_nulls_in_place_but_refuses_a_mixed_list()
    {
        var hop = new DeIdentificationFieldHop("$.name[*].given[*]", "Mask", MaskConfig, null, null, true, null);
        var service = DeIdentificationService();

        var redacted = (string?[])service.DeIdentifyValue(new List<string?> { "Tester", null }, hop)!;
        redacted.Should().HaveCount(2);
        redacted[0].Should().Be((string?)service.DeIdentifyValue("Tester", hop));
        redacted[1].Should().BeNull();
        service.DeIdentifyValue(new List<object?> { "Tester", 7 }, hop).Should().BeNull();   // fails closed
        service.DeIdentifyValue(new List<string?> { null, null }, hop).Should().BeNull();
    }

    // ── Fifth review ──────────────────────────────────────────────────────────────────────────────────────

    private const string FullPatient = """
        {"resourceType":"Patient","id":"p1",
         "name":[{"family":"Sable","given":["DB","Tester"]}],
         "address":[{"line":["1 Main St"],"city":"Springfield","postalCode":"62704"}],
         "contact":[{"name":{"family":"Kinsman","given":["Annabel"]}}],
         "managingOrganization":{"reference":"Organization/org-77","display":"Lakeside Clinic"}}
        """;

    private static Task<DeIdentificationResult> DeIdentifyFullPatientAsync(params TransformationRule[] rules) =>
        DeIdentificationService(rules).DeIdentifyAsync(
            new DeIdentificationRequest("Patient", "p1", FullPatient, [], ProfileId), CancellationToken.None);

    private const string HashConfig = """{"mode":"hash"}""";
    private const string RedactConfig = """{"mode":"redact"}""";

    // #1 — restoring an element never brings back data a rule at, above or beneath it redacted.

    [Theory]
    [InlineData("$.name", "$.name[*].family")]
    [InlineData("Patient.name", "name.family")]
    [InlineData("name[0]", "$.name[0].family")]
    public async Task A_deferrable_rule_on_name_never_restores_a_hashed_family(string ancestor, string descendant)
    {
        // Both orders: Hash first (it fires, so "name" is not restorable) and Redact first (the Hash never fires —
        // the name is already gone — yet the original subtree still holds the family).
        foreach (var rules in new[]
        {
            new[] { PreMapping(descendant, HashConfig), PreMapping(ancestor, RedactConfig) },
            new[] { PreMapping(ancestor, RedactConfig), PreMapping(descendant, HashConfig) },
        })
        {
            var result = await DeIdentifyFullPatientAsync(rules);

            (result.DeferrableOriginalJson ?? string.Empty).Should().NotContain("Sable");
            result.Json.Should().NotContain("Sable");
        }
    }

    [Fact]
    public async Task A_deferrable_rule_on_address_never_restores_a_generalized_postal_code()
    {
        foreach (var rules in new[]
        {
            new[] { PreMapping("$.address[*].postalCode", """{"mode":"generalizeZip3"}"""), PreMapping("$.address", RedactConfig) },
            new[] { PreMapping("$.address", RedactConfig), PreMapping("$.address[*].postalCode", """{"mode":"generalizeZip3"}""") },
        })
        {
            var result = await DeIdentifyFullPatientAsync(rules);

            (result.DeferrableOriginalJson ?? string.Empty).Should().NotContain("62704");
        }
    }

    [Fact]
    public async Task A_deferrable_rule_never_restores_a_reference_the_profile_rewrites()
    {
        // The profile hashes Organization ids, so every "Organization/…" reference is rewritten. Redact on the whole
        // managingOrganization must not bring the original reference back inside the restored object.
        var repository = new Mock<ITransformationRuleRepository>();
        repository.Setup(r => r.GetPreMappingRulesAsync(ProfileId, "Patient", It.IsAny<CancellationToken>()))
            .ReturnsAsync([PreMapping("$.managingOrganization", RedactConfig)]);
        repository.Setup(r => r.GetPreMappingRulesAsync(ProfileId, "Organization", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new TransformationRule(
                TransformScope.ResourceType, TransformNodeType.HashingMasking, HashConfig, resourceType: "Organization",
                sourceField: "$.id", executionPhase: TransformExecutionPhase.PreMapping, deIdentificationProfileId: ProfileId)]);

        var result = await new SafeHarborDeIdentificationService(repository.Object).DeIdentifyAsync(
            new DeIdentificationRequest("Patient", "p1", FullPatient, [], ProfileId), CancellationToken.None);

        result.DeferrableOriginalJson.Should().NotBeNull().And.NotContain("org-77");
    }

    [Fact]
    public async Task A_rewritten_reference_beside_a_deferrable_sibling_stays_rewritten()
    {
        // display (Mask) and reference (rewritten) are siblings: display is restorable, the reference is not.
        var repository = new Mock<ITransformationRuleRepository>();
        repository.Setup(r => r.GetPreMappingRulesAsync(ProfileId, "Patient", It.IsAny<CancellationToken>()))
            .ReturnsAsync([PreMapping("$.managingOrganization.display", MaskConfig)]);
        repository.Setup(r => r.GetPreMappingRulesAsync(ProfileId, "Organization", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new TransformationRule(
                TransformScope.ResourceType, TransformNodeType.HashingMasking, HashConfig, resourceType: "Organization",
                sourceField: "$.id", executionPhase: TransformExecutionPhase.PreMapping, deIdentificationProfileId: ProfileId)]);

        var result = await new SafeHarborDeIdentificationService(repository.Object).DeIdentifyAsync(
            new DeIdentificationRequest("Patient", "p1", FullPatient, [], ProfileId), CancellationToken.None);

        result.DeferrableOriginalJson.Should().Contain("Lakeside Clinic").And.NotContain("org-77");
    }

    [Fact]
    public async Task Only_the_safe_sibling_is_restored_in_nested_arrays()
    {
        // contact[*].name.given is masked (restorable); contact[*].name.family is hashed (never restored).
        var result = await DeIdentifyFullPatientAsync(
            PreMapping("$.contact[*].name.given", MaskConfig), PreMapping("$.contact[*].name.family", HashConfig));

        result.DeferrableOriginalJson.Should().Contain("Annabel").And.NotContain("Kinsman");
    }

    [Fact]
    public async Task An_ancestor_Remove_is_never_undone_for_a_descendant_column()
    {
        // Mask on given fires first, then Remove deletes the whole name. given overlaps that Remove, so it is not
        // restored and the column is not deferred: it stays removed, exactly as redaction-first writes it.
        var (deIdentified, record) = await RunAsync(
            DeIdentificationService(GivenMaskRule, PreMapping("$.name", """{"mode":"remove"}""")),
            PatientNameConcat, Column("PatientName", "$.name[*].given"));

        DeIdentificationSnapshots.DeferrableOriginalIfCurrent(
            (ResourceEnvelope)((DeIdentifiedBatch)deIdentified.Payload!).Records.Single()).Should().BeNull();
        record.Values.GetValueOrDefault("PatientName").Should().BeNull();
        JsonSerializer.Serialize(record.Values).Should().NotContain("Tester");
    }

    [Fact]
    public async Task A_safe_sibling_column_is_still_deferred_and_its_hashed_sibling_never_reaches_it()
    {
        var (_, record) = await RunAsync(
            DeIdentificationService(GivenMaskRule, PreMapping("$.name[*].family", HashConfig)),
            PatientNameConcat, Column("PatientName", "$.name[*].given"), Column("Family", "$.name[*].family"));

        record.Values["PatientName"].Should().Be("*****ster");                       // deferred, as before
        ((string)record.Values["Family"]!).Should().StartWith("anon-");              // hashed, redaction-first
        JsonSerializer.Serialize(record.Values).Should().NotContain("Sable").And.NotContain("Tester");
    }

    [Theory]
    [InlineData("name.given", new[] { "name.given" }, true)]
    [InlineData("name.given", new[] { "name.given", "name" }, false)]          // ancestor overlaps
    [InlineData("name", new[] { "name", "name.family" }, false)]               // descendant overlaps
    [InlineData("name.given", new[] { "name.given", "name.family" }, true)]    // sibling only
    [InlineData("name.given", new[] { "name.family" }, false)]                 // nothing at the element itself
    public void CanRestore_follows_ancestor_and_descendant_overlap(string element, string[] maskedPaths, bool expected)
    {
        var hops = maskedPaths
            .Select(path => new DeIdentificationFieldHop(path, DeIdentificationStrategyNames.Mask, MaskConfig, null, null, true, null))
            .ToArray();

        DeIdentificationDeferral.CanRestore(element, hops, "Patient").Should().Be(expected);
    }

    [Theory]
    [InlineData("Hash")]
    [InlineData("GeneralizeZip3")]
    [InlineData("Hash:Reference")]
    public void CanRestore_refuses_any_non_deferrable_hop_on_the_same_element(string strategy)
    {
        DeIdentificationFieldHop[] hops =
        [
            new("$.name[*].given", DeIdentificationStrategyNames.Mask, MaskConfig, null, null, true, null),
            new("Patient.name.given", strategy, "{}", null, null, true, null),
        ];

        DeIdentificationDeferral.CanRestore("$.name[*].given[*]", hops, "Patient").Should().BeFalse();
    }

    // #2 — a de-identification service that cannot redact never hands the value back.

    [Fact]
    public void The_pass_through_service_fails_closed()
    {
        new PassThroughDeIdentificationService()
            .DeIdentifyValue("DB Tester", new DeIdentificationFieldHop("$.name[*].given[*]", "Mask", MaskConfig, null, null, true, null))
            .Should().BeNull();
    }

    [Fact]
    public async Task A_pass_through_service_registered_for_Mapping_drops_the_value_and_the_run_reports_it()
    {
        // De-identification ran with Safe Harbor (so the column is deferred), but the Mapping node was handed the
        // pass-through service: it cannot redact, so nothing unredacted may be written.
        _mappingDeIdentification = new PassThroughDeIdentificationService();

        var (_, record) = await RunAsync(DeIdentificationService(GivenMaskRule), PatientNameConcat, Column("PatientName", "$.name[*].given"));

        record.Values.GetValueOrDefault("PatientName").Should().BeNull();
        JsonSerializer.Serialize(record.Values).Should().NotContain("Tester");
        var redaction = _lineage.Single(hop => hop.DestinationField == "PatientName" && hop.NodeType == "DeIdentification:Mask");
        redaction.Success.Should().BeFalse();
        ((string[])_mappingOutput!.Metadata!["skippedResourceTypes"]!).Should().ContainSingle()
            .Which.Should().Contain("Patient: 1 column value(s) were left empty");
    }

    // #3 — failures around a deferred column never let the unredacted value out.

    [Fact]
    public async Task A_redaction_that_throws_drops_the_value_records_it_and_never_leaks_its_message()
    {
        _mappingDeIdentification = new ThrowingDeIdentificationService();

        var (_, record) = await RunAsync(DeIdentificationService(GivenMaskRule), PatientNameConcat, Column("PatientName", "$.name[*].given"));

        record.Values.GetValueOrDefault("PatientName").Should().BeNull();
        var redaction = _lineage.Single(hop => hop.DestinationField == "PatientName" && hop.NodeType == "DeIdentification:Mask");
        redaction.Success.Should().BeFalse();
        redaction.ErrorMessage.Should().Contain("withheld").And.NotContain("Tester");
        ((string[])_mappingOutput!.Metadata!["skippedResourceTypes"]!).Should().ContainSingle();
        _logs.Should().Contain(line => line.Contains(nameof(InvalidOperationException)));
        _logs.Should().NotContain(line => line.Contains("Tester"));
    }

    [Fact]
    public async Task A_deferred_step_that_throws_with_PassThrough_still_ends_redacted()
    {
        // The step throws with the unredacted value in its message. PassThrough keeps the value it was handed —
        // the unredacted names — so the redaction after the chain is what makes it safe; the message never escapes.
        _nodeOverrides = [new ThrowingStringNormalizationNode()];
        var chain = new Dictionary<string, IReadOnlyList<TransformationRule>>
        {
            ["PatientName"] =
            [
                new TransformationRule(TransformScope.Field, TransformNodeType.StringNormalization, """{"trim":"true"}""",
                    resourceType: "Patient", destinationField: "PatientName", order: 0, errorPolicy: TransformErrorPolicy.PassThrough),
                new TransformationRule(TransformScope.Field, TransformNodeType.ConcatenationTemplating,
                    """{"mode":"concat","separator":"","template":"{0} {1}"}""", resourceType: "Patient", destinationField: "PatientName", order: 1),
            ],
        };

        var (_, record) = await RunAsync(DeIdentificationService(GivenMaskRule), chain, Column("PatientName", "$.name[*].given"));

        record.Values["PatientName"].Should().Be("*****ster");
        var failed = _lineage.Single(hop => hop.DestinationField == "PatientName" && hop.NodeType == "StringNormalization");
        failed.Success.Should().BeFalse();
        failed.ErrorMessage.Should().NotContain("Tester");
        _lineage.Single(hop => hop.NodeType == "DeIdentification:Mask").Success.Should().BeTrue();
        _logs.Should().NotContain(line => line.Contains("Tester"));
    }

    [Fact]
    public async Task A_deferred_step_that_throws_with_Fail_policy_writes_nothing_unredacted()
    {
        _nodeOverrides = [new ThrowingStringNormalizationNode()];
        var chain = new Dictionary<string, IReadOnlyList<TransformationRule>>
        {
            ["PatientName"] =
            [
                new TransformationRule(TransformScope.Field, TransformNodeType.StringNormalization, """{"trim":"true"}""",
                    resourceType: "Patient", destinationField: "PatientName", order: 0, errorPolicy: TransformErrorPolicy.Fail),
                new TransformationRule(TransformScope.Field, TransformNodeType.ConcatenationTemplating,
                    """{"mode":"concat","separator":"","template":"{0} {1}"}""", resourceType: "Patient", destinationField: "PatientName", order: 1),
            ],
        };

        var (_, record) = await RunAsync(DeIdentificationService(GivenMaskRule), chain, Column("PatientName", "$.name[*].given"));

        JsonSerializer.Serialize(record.Values).Should().NotContain("Tester");
        _logs.Should().NotContain(line => line.Contains("Tester"));
    }

    [Fact]
    public void A_hop_with_malformed_config_fails_closed()
    {
        DeIdentificationService()
            .DeIdentifyValue("DB Tester", new DeIdentificationFieldHop("$.name[*].given[*]", "Mask", "not json", null, null, true, null))
            .Should().BeNull();
    }

    // #5 — deferred chains keep their value types.

    [Theory]
    [InlineData("last", "**ster")]                 // a string: redacted
    [InlineData("first", "DB")]                    // a string within keepLength: left as the mask leaves it
    [InlineData("dedupe", "DB, **ster")]           // a list of strings: each redacted, then written as one value
    public async Task An_ArrayList_step_that_yields_text_is_deferred_and_redacted(string operation, string expected)
    {
        var chain = Chain("PatientName", (TransformNodeType.ArrayListOperations, $$"""{"operation":"{{operation}}"}"""));

        var (_, record) = await RunAsync(DeIdentificationService(GivenMaskRule), chain, Column("PatientName", "$.name[*].given"));

        record.Values["PatientName"].Should().Be(expected);
        _lineage.Where(hop => hop.DestinationField == "PatientName").Should().OnlyContain(hop => hop.Success);
    }

    // #12 — a missing snapshot is visible, never a silent change.

    [Fact]
    public async Task A_missing_snapshot_falls_back_to_redaction_first_and_lineage_says_why()
    {
        _between = envelope => envelope with { };   // a new envelope instance: the snapshot is not carried over

        var (_, record) = await RunAsync(DeIdentificationService(GivenMaskRule), PatientNameConcat, Column("PatientName", "$.name[*].given"));

        record.Values["PatientName"].Should().Be(PreContractConcat("DB, **ster"));
        var fallback = _lineage.Single(hop => hop.NodeType == "DeIdentification:DeferralFallback");
        fallback.Success.Should().BeFalse();
        fallback.ErrorMessage.Should().Contain("not available");
    }

    // #16 — one deferred column that cannot be read does not take the others with it.

    [Fact]
    public async Task One_unreadable_deferred_column_falls_back_alone()
    {
        _mappingEngine = new FailingColumnEngine("Broken");

        var (_, record) = await RunAsync(
            DeIdentificationService(GivenMaskRule),
            new Dictionary<string, IReadOnlyList<TransformationRule>> { ["PatientName"] = [ConcatRule], ["Broken"] = [ConcatRule] },
            Column("PatientName", "$.name[*].given"), Column("Broken", "$.name[*].given"));

        record.Values["PatientName"].Should().Be("*****ster");                       // still deferred
        record.Values["Broken"].Should().Be(PreContractConcat("DB, **ster"));         // redaction-first
        _lineage.Where(hop => hop.NodeType == "DeIdentification:DeferralFallback")
            .Should().ContainSingle().Which.DestinationField.Should().Be("Broken");
    }

    [Fact]
    public async Task Several_unreadable_columns_each_fall_back_and_readable_ones_still_defer()
    {
        _mappingEngine = new FailingColumnEngine("BrokenA", "BrokenB");
        var chains = new Dictionary<string, IReadOnlyList<TransformationRule>>
        {
            ["PatientName"] = [ConcatRule], ["AlsoName"] = [ConcatRule], ["BrokenA"] = [ConcatRule], ["BrokenB"] = [ConcatRule],
        };

        var (_, record) = await RunAsync(
            DeIdentificationService(GivenMaskRule), chains,
            Column("PatientName", "$.name[*].given"), Column("AlsoName", "$.name[*].given"),
            Column("BrokenA", "$.name[*].given"), Column("BrokenB", "$.name[*].given"));

        record.Values["PatientName"].Should().Be("*****ster");
        record.Values["AlsoName"].Should().Be("*****ster");
        _lineage.Where(hop => hop.NodeType == "DeIdentification:DeferralFallback").Select(hop => hop.DestinationField)
            .Should().BeEquivalentTo(["BrokenA", "BrokenB"]);
        JsonSerializer.Serialize(record.Values).Should().NotContain("Tester");
    }

    // #10 — the deferred items always belong to the instance the column holds.

    [Fact]
    public async Task An_empty_first_name_never_borrows_the_given_names_of_another_name()
    {
        _patientJson = """
            {"resourceType":"Patient","id":"p1","name":[{"given":[]},{"given":["Other","Person"]}]}
            """;

        var (_, record) = await RunAsync(DeIdentificationService(GivenMaskRule), PatientNameConcat, Column("PatientName", "$.name[*].given"));

        // First instance = the first name, whose given is empty: nothing of the second name may appear.
        JsonSerializer.Serialize(record.Values).Should().NotContain("Other").And.NotContain("rson");
    }

    // ── Fail closed: a De-identification node never passes data through on a policy with no rules ──────────────

    [Fact]
    public async Task A_policy_with_no_rules_fails_the_node_and_nothing_reaches_Mapping()
    {
        // The state the old canvas delete could leave behind: the step still saved, its policy emptied. It used to
        // log a warning, pass every resource through unredacted and report success.
        var run = () => RunAsync(DeIdentificationService(), PatientNameConcat, Column("PatientName", "$.name[*].given"));

        var failure = (await run.Should().ThrowAsync<InvalidOperationException>()).Which;
        failure.Message.Should().Contain("has no rules").And.Contain("nothing was written");
        failure.Message.Should().NotContain("Tester").And.NotContain("Sable");   // names the policy, never the data
        _mappingOutput.Should().BeNull();
    }

    [Fact]
    public async Task A_policy_that_does_not_exist_fails_the_node_too()
    {
        var node = Node(WorkflowNodeTypes.DeIdentification, WorkflowNodeCategory.Compliance,
            new { profileId = Guid.NewGuid().ToString() });   // no such policy: it has no rules either

        var run = () => new DeIdentificationNodeExecutor(DeIdentificationService(GivenMaskRule))
            .ExecuteAsync(new WorkflowExecutionContext(Guid.NewGuid(), "test"), node, [PatientSource()], CancellationToken.None);

        await run.Should().ThrowAsync<InvalidOperationException>().WithMessage("*has no rules*");
    }

    [Fact]
    public async Task A_service_that_cannot_redact_fails_the_node_rather_than_passing_data_through()
    {
        var node = Node(WorkflowNodeTypes.DeIdentification, WorkflowNodeCategory.Compliance, new { profileId = ProfileId.ToString() });

        var run = () => new DeIdentificationNodeExecutor(new PassThroughDeIdentificationService())
            .ExecuteAsync(new WorkflowExecutionContext(Guid.NewGuid(), "test"), node, [PatientSource()], CancellationToken.None);

        await run.Should().ThrowAsync<InvalidOperationException>().WithMessage("*has no rules*");
    }

    [Fact]
    public async Task A_policy_whose_rules_cover_other_resource_types_still_runs_with_its_warning()
    {
        // Rules exist (for Observation, say) but none for Patient: a valid policy, unchanged behaviour.
        var repository = new Mock<ITransformationRuleRepository>();
        repository.Setup(r => r.GetPreMappingRulesAsync(ProfileId, It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        repository.Setup(r => r.HasPreMappingRulesAsync(ProfileId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var node = Node(WorkflowNodeTypes.DeIdentification, WorkflowNodeCategory.Compliance, new { profileId = ProfileId.ToString() });

        var output = await new DeIdentificationNodeExecutor(new SafeHarborDeIdentificationService(repository.Object))
            .ExecuteAsync(new WorkflowExecutionContext(Guid.NewGuid(), "test"), node, [PatientSource()], CancellationToken.None);

        output.Metadata!["redactedResourceCount"].Should().Be(0);
    }

    [Fact]
    public async Task A_policy_with_rules_runs_and_redacts_as_before()
    {
        var (deIdentified, _) = await RunAsync(DeIdentificationService(GivenMaskRule), PatientNameConcat, Column("PatientName", "$.name[*].given"));

        deIdentified.Metadata!["redactedResourceCount"].Should().Be(1);
    }

    private static WorkflowNodeOutput PatientSource() => new(
        Guid.NewGuid(), WorkflowNodeTypes.EpicSource,
        new ResourceBatch([new ResourceEnvelope("Patient", "p1", PatientJson)]), WorkflowDataContract.ResourceBatch);

    /// <summary>Runs Safe Harbor's real redaction for the De-identification node, but throws — quoting the value —
    /// whenever the Mapping node asks it to redact a deferred column.</summary>
    private sealed class ThrowingDeIdentificationService : IDeIdentificationService
    {
        public Task<DeIdentificationResult> DeIdentifyAsync(DeIdentificationRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public object? DeIdentifyValue(object? value, DeIdentificationFieldHop hop) =>
            throw new InvalidOperationException($"cannot redact '{value}'");

        public Task<bool> HasRulesAsync(Guid profileId, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    /// <summary>A StringNormalization step that throws with its input in the message.</summary>
    private sealed class ThrowingStringNormalizationNode : ITransformNode
    {
        public TransformNodeType NodeType => TransformNodeType.StringNormalization;
        public bool AcceptsCollections => false;
        public bool AcceptsStructuredValue => false;

        public TransformResult Execute(object? value, IReadOnlyDictionary<string, string> config, string? secret) =>
            throw new FormatException($"bad input '{value}'");
    }

    /// <summary>The real engine, except that reading any of the given columns from a pre-de-identification
    /// snapshot (the payload still holds "Tester") throws.</summary>
    private sealed class FailingColumnEngine : IJsonMappingEngine
    {
        private readonly JsonMappingEngine _inner = new();
        private readonly string[] _failing;

        public FailingColumnEngine(params string[] failing) => _failing = failing;

        public MappingTestResultDto Map(string sourceJson, IReadOnlyCollection<MappingFieldDto> fields, IReadOnlyDictionary<string, object?>? systemValues = null) =>
            sourceJson.Contains("Tester") && fields.Any(field => _failing.Contains(field.TargetField))
                ? throw new InvalidOperationException("cannot read " + sourceJson)
                : _inner.Map(sourceJson, fields, systemValues);
    }

    /// <summary>What a NON-deferred column's Concatenation ("{0} {1}") writes for an array path — its long-standing
    /// contract: the one ", "-joined value fills {0}. Computed by the real node, not hardcoded.</summary>
    private static object? PreContractConcat(string joined) =>
        new ConcatenationTemplatingNode().Execute(
            joined, new Dictionary<string, string> { ["mode"] = "concat", ["separator"] = "", ["template"] = "{0} {1}" }, null).Value;

    private static IReadOnlyDictionary<string, IReadOnlyList<TransformationRule>> Chain(string column, params (TransformNodeType Type, string Config)[] steps) =>
        new Dictionary<string, IReadOnlyList<TransformationRule>>
        {
            [column] = steps
                .Select((step, order) => new TransformationRule(
                    TransformScope.Field, step.Type, step.Config, resourceType: "Patient", destinationField: column, order: order))
                .ToArray(),
        };

    [Theory]
    [InlineData("name.given")]           // bare, as the seeded Safe Harbor rules are authored
    [InlineData("Patient.name.given")]   // resource-qualified, as the transformation-rule screens author them
    [InlineData("$.name[0].given")]      // indexed
    public async Task Every_rule_path_convention_is_recognised(string rulePath)
    {
        var (_, record) = await RunAsync(
            DeIdentificationService(PreMapping(rulePath, MaskConfig)), PatientNameConcat, Column("PatientName", "$.name[*].given"));

        record.Values["PatientName"].Should().Be("*****ster");
    }

    [Fact]
    public async Task A_chain_with_a_step_that_is_not_text_reshaping_keeps_redaction_first()
    {
        var chain = Chain("PatientName",
            (TransformNodeType.ConcatenationTemplating, """{"mode":"concat","separator":"","template":"{0} {1}"}"""),
            (TransformNodeType.DefaultNullHandling, "{}"));

        var (_, record) = await RunAsync(DeIdentificationService(GivenMaskRule), chain, Column("PatientName", "$.name[*].given"));

        record.Values["PatientName"].Should().Be(PreContractConcat("DB, **ster"));   // each name masked first, as before this PR
    }

    [Fact]
    public async Task A_Hash_rule_keeps_redaction_first_so_pseudonyms_still_match_hashed_references()
    {
        var (_, record) = await RunAsync(
            DeIdentificationService(PreMapping("$.name[*].given[*]", """{"mode":"hash"}""")), PatientNameConcat,
            Column("PatientName", "$.name[*].given"));

        var value = (string)record.Values["PatientName"]!;
        value.Should().NotContain("Tester");
        // Two per-name pseudonyms (each hashed before any transform), not one hash of "DB Tester".
        System.Text.RegularExpressions.Regex.Matches(value, "anon-[0-9a-f]+").Should().HaveCount(2);
    }

    [Fact]
    public async Task A_Generalize_rule_keeps_redaction_first_because_it_reads_raw_character_positions()
    {
        // Reformats YYYY-MM-DD to MM/DD/YYYY. Applied AFTER the transform, generalize-to-year would take the first
        // four characters of "03/15/1980" — "03/1", exposing the month. Redaction-first gives "1980" (no match).
        var chain = Chain("BirthYear", (TransformNodeType.StringNormalization,
            """{"regexPattern":"^(\\d{4})-(\\d{2})-(\\d{2})$","regexReplacement":"$2/$3/$1"}"""));

        var (_, record) = await RunAsync(
            DeIdentificationService(PreMapping("$.birthDate", """{"mode":"generalizeDateToYear"}""")), chain,
            Column("BirthYear", "$.birthDate"));

        record.Values["BirthYear"].Should().Be("1980");
    }

    [Fact]
    public async Task A_joined_column_whose_parts_have_different_rules_keeps_redaction_first()
    {
        var (_, record) = await RunAsync(
            DeIdentificationService(GivenMaskRule, PreMapping("$.name[*].family", """{"mode":"redact","token":"[X]"}""")),
            FullNameUpper, JoinedFullName());

        var value = (string)record.Values["FullName"]!;
        value.Should().Contain("**STER").And.Contain("[X]").And.NotContain("TESTER").And.NotContain("SABLE");
    }

    [Fact]
    public async Task A_failing_step_on_a_deferred_column_never_writes_its_error_text_to_lineage()
    {
        var chain = Chain("PatientName", (TransformNodeType.StringNormalization, """{"regexPattern":"("}"""));

        await RunAsync(DeIdentificationService(GivenMaskRule), chain, Column("PatientName", "$.name[*].given"));

        var failed = _lineage.Single(hop => hop.DestinationField == "PatientName" && !hop.Success && hop.NodeType == "StringNormalization");
        failed.ErrorMessage.Should().StartWith("This step failed. Its message is withheld");
        JsonSerializer.Serialize(_lineage).Should().NotContain("Tester");
    }

    [Fact]
    public async Task An_ArrayList_count_is_not_deferred_so_it_stays_a_real_number()
    {
        // "count" yields a number no redaction applies to. Deferring it would have to drop it (it used to be written
        // as NULL); it keeps redaction-first instead, where counting the redacted items gives the same number.
        var chain = Chain("NameCount", (TransformNodeType.ArrayListOperations, """{"operation":"count"}"""));

        var (_, record) = await RunAsync(DeIdentificationService(GivenMaskRule), chain, Column("NameCount", "$.name[*].given[*]"));

        record.Values["NameCount"].Should().Be(2);
        _lineage.Where(hop => hop.DestinationField == "NameCount").Should().OnlyContain(hop => hop.Success);
        _lineage.Should().Contain(hop => hop.DestinationField == "NameCount" && hop.NodeType == "DeIdentification:Mask");
        _mappingOutput!.Metadata!["skippedResourceTypes"].Should().BeNull();
    }

    // ── Third review ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_node_that_rewrites_the_payload_after_De_identification_makes_the_copy_stale_and_it_is_ignored()
    {
        // Stands in for any later node that rewrites the payload — it never had to know about the snapshot for this
        // to be safe: the new envelope instance has none.
        _between = envelope => envelope with { Payload = ((string)envelope.Payload).Replace("\"official\"", "\"usual\"") };

        var (_, record) = await RunAsync(DeIdentificationService(GivenMaskRule), PatientNameConcat, Column("PatientName", "$.name[*].given"));

        // Redaction-first, from the rewritten (redacted) payload — the pre-PR behaviour, never the raw name.
        record.Values["PatientName"].Should().Be(PreContractConcat("DB, **ster"));
    }

    [Fact]
    public void The_copy_is_used_only_while_the_payload_is_still_the_one_it_was_captured_with()
    {
        var envelope = new ResourceEnvelope("Patient", "p1", "{\"redacted\":true}");
        DeIdentificationSnapshots.Attach(envelope, PatientJson, "{\"redacted\":true}");

        DeIdentificationSnapshots.DeferrableOriginalIfCurrent(envelope).Should().Be(PatientJson);
        // Replaced payload: a new envelope instance, so no snapshot at all.
        DeIdentificationSnapshots.DeferrableOriginalIfCurrent(envelope with { Payload = "{\"redacted\":1}" }).Should().BeNull();

        // A payload that could be mutated in place (anything but the immutable redacted string) is refused outright,
        // even while it still reads the same.
        var node = System.Text.Json.Nodes.JsonNode.Parse("{\"redacted\":true}")!.AsObject();
        var live = new ResourceEnvelope("Patient", "p2", node);
        DeIdentificationSnapshots.Attach(live, PatientJson, node.ToJsonString());
        DeIdentificationSnapshots.DeferrableOriginalIfCurrent(live).Should().BeNull();
    }

    [Fact]
    public async Task No_copy_is_kept_when_only_a_non_deferrable_redaction_fired()
    {
        var (deIdentified, _) = await RunAsync(
            DeIdentificationService(PreMapping("$.name[*].given[*]", """{"mode":"hash"}""")), PatientNameConcat,
            Column("PatientName", "$.name[*].given"));

        DeIdentificationSnapshots.DeferrableOriginalIfCurrent((ResourceEnvelope)((DeIdentifiedBatch)deIdentified.Payload!).Records.Single()).Should().BeNull();
    }

    [Fact]
    public async Task A_deferred_column_the_unredacted_read_cannot_resolve_keeps_its_redacted_value()
    {
        // The snapshot is current but does not carry the column's element. The column must NOT be written empty: it
        // keeps the redacted value (redaction first, never raw), and lineage says the deferral fell back.
        _between = envelope =>
        {
            var replacement = envelope with { };
            DeIdentificationSnapshots.Attach(replacement, """{"resourceType":"Patient","id":"p1"}""", (string)envelope.Payload);
            return replacement;
        };

        var (_, record) = await RunAsync(DeIdentificationService(GivenMaskRule), PatientNameConcat, Column("PatientName", "$.name[*].given"));

        record.Values["PatientName"].Should().Be(PreContractConcat("DB, **ster"));
        var fallback = _lineage.Single(hop => hop.DestinationField == "PatientName" && hop.NodeType == "DeIdentification:DeferralFallback");
        fallback.Success.Should().BeFalse();
        fallback.ErrorMessage.Should().Contain("could not be read").And.NotContain("Tester");
    }

    [Fact]
    public void Remove_drops_a_whole_list_never_a_list_of_nulls()
    {
        var service = DeIdentificationService();
        var remove = Hop("""{"mode":"remove"}""");

        service.DeIdentifyValue(new List<string?> { "DB", "Tester" }, remove).Should().BeNull();
        service.DeIdentifyValue("DB Tester", remove).Should().BeNull();
        service.DeIdentifyValue(new List<string?> { null, null }, Hop()).Should().BeNull();   // nothing left = gone
    }

    [Fact]
    public void Strategy_names_match_the_Safe_Harbor_enum_so_hops_and_the_allowlist_cannot_drift()
    {
        var enumNames = Enum.GetNames<DeIdentificationStrategy>();
        var constants = typeof(DeIdentificationStrategyNames).GetFields()
            .Where(field => field.IsLiteral && field.Name != nameof(DeIdentificationStrategyNames.ReferenceSuffix))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToArray();

        constants.Should().BeEquivalentTo(enumNames);
        DeIdentificationStrategyNames.DeferrableAfterTransformations.Should().BeSubsetOf(enumNames)
            .And.BeEquivalentTo(new[] { "Mask", "Redact", "Remove" });
    }

    /// <summary>Collects every formatted log line (with the exception message, if any) the Mapping node writes.</summary>
    private sealed class CapturingLoggerFactory : Microsoft.Extensions.Logging.ILoggerFactory
    {
        private readonly List<string> _lines;

        public CapturingLoggerFactory(List<string> lines) => _lines = lines;

        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new Logger(_lines);

        public void AddProvider(Microsoft.Extensions.Logging.ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        private sealed class Logger : Microsoft.Extensions.Logging.ILogger
        {
            private readonly List<string> _lines;

            public Logger(List<string> lines) => _lines = lines;

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

            public void Log<TState>(
                Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (_lines)
                {
                    _lines.Add(formatter(state, exception) + (exception is null ? string.Empty : " | " + exception.Message));
                }
            }
        }
    }

    private sealed class CollectingLineageDispatcher : ILineageCaptureDispatcher
    {
        private readonly List<LineageHopEntryDto> _hops;
        public CollectingLineageDispatcher(List<LineageHopEntryDto> hops) => _hops = hops;

        public Task EnqueueAsync(LineageCaptureCommand command, CancellationToken cancellationToken)
        {
            _hops.AddRange(command.Entries);
            return Task.CompletedTask;
        }
    }
}

/// <summary>A path that stops ON an array of plain values ("$.name[*].given") records the array's ITEMS in
/// PrimitiveArrayItems — the same parts "...given[*]" yields — for a column whose De-identification is deferred. The
/// long-standing RawArrayValues contract (one ", "-joined value per array) and the column's own value are unchanged.</summary>
public sealed class JsonMappingEngineArrayItemPartsTests
{
    private const string TwoNames = """
        {"resourceType":"Patient","name":[{"given":["DB","Tester"]},{"given":["Dee","Bee"]},{"given":[]},{"given":["Solo"]}],
         "address":[{"line":["1 Main St","Apt 2"]}],"gender":"male",
         "contact":[{"name":{"given":["Ann",null,"Lee"]}}]}
        """;

    private static MappingTestResultDto Map(string target, string path, ArrayPolicy policy, string[] ancestors, string? format = null) =>
        new JsonMappingEngine().Map(TwoNames,
        [
            new MappingFieldDto(target, path, MappingValueType.String, false, null, format, "Patient", "dbo.Patient",
                ArrayPolicy: policy, ArrayAncestors: ancestors),
        ]);

    [Fact]
    public void First_instance_records_that_names_given_items()
    {
        var result = Map("PatientName", "$.name[*].given", ArrayPolicy.FirstItem, ["name"]);

        result.PrimitiveArrayItems!["PatientName"].Should().Equal("DB", "Tester");
    }

    [Fact]
    public void RawArrayValues_and_the_column_value_keep_their_long_standing_contract()
    {
        var result = Map("PatientName", "$.name[*].given", ArrayPolicy.FirstItem, ["name"]);

        // One ", "-joined value per array — never the bare items.
        result.RawArrayValues!["PatientName"].Should().Contain("DB, Tester").And.NotContain("DB").And.NotContain("Tester");
        result.Values["PatientName"].Should().Be("DB, Tester");
    }

    [Fact]
    public void Nth_instance_records_that_instances_items()
    {
        // index=N is zero-based: index=1 is the second name. Same narrowing as the "...given[*]" path gets.
        var arrayPath = Map("PatientName", "$.name[*].given", ArrayPolicy.FirstItem, ["name"], format: "index=1");
        var itemPath = Map("PatientName", "$.name[*].given[*]", ArrayPolicy.FirstItem, ["name"], format: "index=1");

        arrayPath.PrimitiveArrayItems!["PatientName"].Should().Equal("Dee", "Bee");
        arrayPath.PrimitiveArrayItems!["PatientName"].Should().Equal(itemPath.RawArrayValues!["PatientName"]);
    }

    [Fact]
    public void An_empty_array_records_no_items()
    {
        var result = new JsonMappingEngine().Map("""{"resourceType":"Patient","name":[{"given":[]}]}""",
        [
            new MappingFieldDto("PatientName", "$.name[*].given", MappingValueType.String, false, null, null, "Patient",
                "dbo.Patient", ArrayPolicy: ArrayPolicy.FirstItem, ArrayAncestors: ["name"]),
        ]);

        (result.PrimitiveArrayItems?.GetValueOrDefault("PatientName") ?? []).Should().BeEmpty();
    }

    [Fact]
    public void A_single_item_array_records_its_one_item()
    {
        // index=3 is the fourth name, ["Solo"] (index=2 is the third name, whose given is empty).
        var result = Map("PatientName", "$.name[*].given", ArrayPolicy.FirstItem, ["name"], format: "index=3");

        result.PrimitiveArrayItems!["PatientName"].Should().Equal("Solo");
    }

    [Fact]
    public void A_null_item_is_kept_in_its_position()
    {
        var result = Map("ContactName", "$.contact[*].name.given", ArrayPolicy.FirstItem, ["contact"]);

        result.PrimitiveArrayItems!["ContactName"].Should().HaveCount(3);
        result.PrimitiveArrayItems!["ContactName"][0].Should().Be("Ann");
        result.PrimitiveArrayItems!["ContactName"][2].Should().Be("Lee");
    }

    private static MappingTestResultDto MapNames(string namesJson, ArrayPolicy policy = ArrayPolicy.FirstItem, string? format = null) =>
        new JsonMappingEngine().Map($$"""{"resourceType":"Patient","name":{{namesJson}}}""",
        [
            new MappingFieldDto("PatientName", "$.name[*].given", MappingValueType.String, false, null, format, "Patient",
                "dbo.Patient", ArrayPolicy: policy, ArrayAncestors: ["name"]),
        ]);

    private static IReadOnlyList<object?> ItemsOf(MappingTestResultDto result) =>
        result.PrimitiveArrayItems?.GetValueOrDefault("PatientName") ?? [];

    [Fact]
    public void An_empty_first_instance_yields_no_items_not_the_second_instances()
    {
        var result = MapNames("""[{"given":[]},{"given":["Tester"]}]""");

        ItemsOf(result).Should().BeEmpty();
        result.PrimitiveArrayItems.Should().ContainKey("PatientName");   // read, and empty — not "unavailable"
    }

    [Fact]
    public void Two_populated_instances_yield_the_first_instances_items()
    {
        ItemsOf(MapNames("""[{"given":["DB"]},{"given":["Tester"]}]""")).Should().Equal("DB");
    }

    [Fact]
    public void Two_empty_instances_yield_no_items()
    {
        ItemsOf(MapNames("""[{"given":[]},{"given":[]}]""")).Should().BeEmpty();
    }

    [Fact]
    public void A_missing_array_is_not_an_instance_so_the_populated_one_is_used_by_the_column_and_its_items_alike()
    {
        var result = MapNames("""[{"family":"Sable"},{"given":["Tester"]}]""");

        ItemsOf(result).Should().Equal("Tester");
        result.Values["PatientName"].Should().Be("Tester");   // the column's own value comes from the same instance
    }

    [Fact]
    public void A_single_parent_with_several_values_yields_them_all()
    {
        ItemsOf(MapNames("""[{"given":["DB","Tester","Jr"]}]""")).Should().Equal("DB", "Tester", "Jr");
    }

    [Fact]
    public void Nth_instance_selects_the_empty_array_itself_rather_than_skipping_it()
    {
        ItemsOf(MapNames("""[{"given":["DB"]},{"given":[]},{"given":["Tester"]}]""", format: "index=1")).Should().BeEmpty();
    }

    [Fact]
    public void All_records_records_every_item()
    {
        var result = Map("PatientName", "$.name[*].given", ArrayPolicy.RepeatParent, ["name"]);

        result.PrimitiveArrayItems!["PatientName"].Should().Equal("DB", "Tester", "Dee", "Bee", "Solo");
    }

    [Fact]
    public void Address_lines_work_the_same_way()
    {
        var result = Map("Street", "$.address[*].line", ArrayPolicy.FirstItem, ["address"]);

        result.PrimitiveArrayItems!["Street"].Should().Equal("1 Main St", "Apt 2");
    }

    [Fact]
    public void A_plain_value_or_an_array_of_objects_records_no_items()
    {
        var gender = Map("Gender", "$.gender", ArrayPolicy.Scalar, []);
        var names = Map("Names", "$.name", ArrayPolicy.FirstItem, []);

        gender.PrimitiveArrayItems.Should().BeNull();
        gender.RawArrayValues!["Gender"].Should().Equal("male");
        names.PrimitiveArrayItems.Should().BeNull();
    }
}
