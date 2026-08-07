using System.Text.Json;
using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Mapping;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services.Transforms;
using FHIRBridge.Application.Services.Transforms.Nodes;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Application.Workflows.Catalog;
using FHIRBridge.Runtime.Application.Workflows.Payloads;
using FHIRBridge.Runtime.Domain.Enums;
using FHIRBridge.Runtime.Domain.Workflows;
using FHIRBridge.Runtime.Infrastructure.Workflows.Executors;
using FluentAssertions;
using Moq;
using ResourceEnvelope = FHIRBridge.Runtime.Application.Workflows.Payloads.ResourceEnvelope;

namespace FHIRBridge.Runtime.UnitTests.Workflows;

/// <summary>
/// Covers the fix for a real production bug: MappingNodeExecutor used to emit SeparateDestination child-table
/// rows as separate flat MappedDestinationRecords carrying their OWN DestinationObject (e.g. "PatientName") —
/// but MappedSqlServerDestinationWriter.WriteAsync resolves a single target table for the whole batch, so those
/// child rows were written straight into the PARENT's table, failing with "Invalid column name" for every
/// child-only column. Child rows must instead travel as MappedChildTableRecords attached to the parent row.
/// Also covers the multi-resource mapping fix: a destination selecting more than one resource (Patient +
/// Observation + Condition) previously had only one MappingProfile wired to its single MappingNode — every OTHER
/// resource type silently got that one profile's fields applied to it too (only "Id" ever matched, since it's
/// the one property every FHIR resource happens to share; everything else came back empty). MappingNodeExecutor
/// must resolve one profile per resource type and apply only the matching one, skipping any resource type with
/// no configured mapping.
/// </summary>
public sealed class MappingNodeExecutorTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Child_table_rows_are_attached_to_the_parent_record_not_emitted_flat()
    {
        var fields = new[]
        {
            new MappingFieldDto("Active", "$.active", MappingValueType.Boolean, IsRequired: false, DefaultValue: null,
                Format: "directField", ResourceType: "Patient", DestinationObject: "Patient", ArrayPolicy: ArrayPolicy.Scalar),
            new MappingFieldDto("family", "$.name[*].family", MappingValueType.String, IsRequired: false, DefaultValue: null,
                Format: "directField", ResourceType: "Patient", DestinationObject: "PatientName",
                ArrayPolicy: ArrayPolicy.SeparateDestination, ParentTable: "Patient", ParentKeyColumn: "Id", ForeignKeyColumn: "PatientId"),
        };
        var engine = new FakeJsonMappingEngine(new MappingTestResultDto(
            Values: new Dictionary<string, object?> { ["Active"] = true },
            Errors: [],
            Rows: null,
            ChildTables: [new MappingChildTableDto("PatientName", [new Dictionary<string, object?> { ["family"] = "Lopez", ["RowIndex"] = "0" }])]));
        var executor = new MappingNodeExecutor(engine, mappingMaterializer: null, configurationRepository: null);
        var node = CreateNode("Patient", "Patient", fields);
        var upstream = UpstreamWith(new ResourceEnvelope("Patient", "p1", """{"resourceType":"Patient"}"""));

        var output = await executor.ExecuteAsync(CreateContext(), node, [upstream], CancellationToken.None);

        var batch = (MappedRecordBatch)output.Payload!;
        batch.Records.Should().HaveCount(1, "child rows must attach to the parent record, not become their own top-level record");
        var record = (MappedDestinationRecord)batch.Records.Single();
        record.DestinationObject.Should().Be("Patient");
        record.Values["Active"].Should().Be(true);
        record.ChildTables.Should().HaveCount(1);
        var childTable = record.ChildTables!.Single();
        childTable.TableName.Should().Be("PatientName");
        childTable.ForeignKeyColumn.Should().Be("PatientId");
        childTable.ParentKeyColumn.Should().Be("Id");
        childTable.Rows.Single()["family"].Should().Be("Lopez");
    }

    [Fact]
    public async Task Child_table_with_no_ForeignKeyColumn_metadata_is_dropped_not_written()
    {
        var fields = new[]
        {
            new MappingFieldDto("Active", "$.active", MappingValueType.Boolean, IsRequired: false, DefaultValue: null,
                Format: "directField", ResourceType: "Patient", DestinationObject: "Patient", ArrayPolicy: ArrayPolicy.Scalar),
            new MappingFieldDto("family", "$.name[*].family", MappingValueType.String, IsRequired: false, DefaultValue: null,
                Format: "directField", ResourceType: "Patient", DestinationObject: "PatientName", ArrayPolicy: ArrayPolicy.SeparateDestination),
        };
        var engine = new FakeJsonMappingEngine(new MappingTestResultDto(
            Values: new Dictionary<string, object?> { ["Active"] = true },
            Errors: [],
            Rows: null,
            ChildTables: [new MappingChildTableDto("PatientName", [new Dictionary<string, object?> { ["family"] = "Lopez" }])]));
        var executor = new MappingNodeExecutor(engine, mappingMaterializer: null, configurationRepository: null);
        var node = CreateNode("Patient", "Patient", fields);
        var upstream = UpstreamWith(new ResourceEnvelope("Patient", "p1", """{"resourceType":"Patient"}"""));

        var output = await executor.ExecuteAsync(CreateContext(), node, [upstream], CancellationToken.None);

        var batch = (MappedRecordBatch)output.Payload!;
        var record = (MappedDestinationRecord)batch.Records.Single();
        record.ChildTables.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task A_node_dedicated_entirely_to_a_child_table_still_emits_a_record_to_carry_it()
    {
        // No root-level field at all — mapped.Values would be empty — but the child table must still be
        // written somewhere, since the writer needs a parent row write to capture the FK value off of.
        var fields = new[]
        {
            new MappingFieldDto("family", "$.name[*].family", MappingValueType.String, IsRequired: false, DefaultValue: null,
                Format: "directField", ResourceType: "Patient", DestinationObject: "PatientName",
                ArrayPolicy: ArrayPolicy.SeparateDestination, ParentTable: "Patient", ParentKeyColumn: "Id", ForeignKeyColumn: "PatientId"),
        };
        var engine = new FakeJsonMappingEngine(new MappingTestResultDto(
            Values: new Dictionary<string, object?>(),
            Errors: [],
            Rows: null,
            ChildTables: [new MappingChildTableDto("PatientName", [new Dictionary<string, object?> { ["family"] = "Lopez" }])]));
        var executor = new MappingNodeExecutor(engine, mappingMaterializer: null, configurationRepository: null);
        var node = CreateNode("Patient", "Patient", fields);
        var upstream = UpstreamWith(new ResourceEnvelope("Patient", "p1", """{"resourceType":"Patient"}"""));

        var output = await executor.ExecuteAsync(CreateContext(), node, [upstream], CancellationToken.None);

        var batch = (MappedRecordBatch)output.Payload!;
        batch.Records.Should().HaveCount(1);
        ((MappedDestinationRecord)batch.Records.Single()).ChildTables.Should().HaveCount(1);
    }

    [Fact]
    public async Task Resolves_the_profile_by_natural_key_even_when_a_stale_mappingProfileId_is_also_present()
    {
        var sourceConnectionId = Guid.NewGuid();
        var destinationId = Guid.NewGuid();
        var realProfile = new MappingProfile(
            "Patient", "Patient", sourceConnectionId, destinationId, "Patient",
            [new MappingField("Active", "$.active", MappingValueType.Boolean, IsRequired: false, DefaultValue: null, Format: "directField")]);

        var repository = new Mock<IConfigurationRepository>();
        repository.Setup(r => r.FindMappingProfileAsync("Patient", sourceConnectionId, destinationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(realProfile);

        var executor = new MappingNodeExecutor(
            new FakeJsonMappingEngine(new MappingTestResultDto(new Dictionary<string, object?> { ["Active"] = true }, [])),
            mappingMaterializer: null,
            configurationRepository: repository.Object);
        var node = CreateNode("Patient", "Patient", fields: [], extraConfig: new Dictionary<string, object>
        {
            ["sourceConnectionId"] = sourceConnectionId.ToString(),
            ["destinationId"] = destinationId.ToString(),
            // Deliberately wrong/stale — a natural-key match must win over this rather than the executor
            // ever resolving (or falling back to) this id.
            ["mappingProfileId"] = Guid.NewGuid().ToString(),
        });
        var upstream = UpstreamWith(new ResourceEnvelope("Patient", "p1", """{"resourceType":"Patient"}"""));

        var output = await executor.ExecuteAsync(CreateContext(), node, [upstream], CancellationToken.None);

        var batch = (MappedRecordBatch)output.Payload!;
        var record = (MappedDestinationRecord)batch.Records.Single();
        record.Values["Active"].Should().Be(true);
        repository.Verify(
            r => r.GetMappingProfileAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "the natural-key lookup found a profile, so the stale mappingProfileId must never even be looked up");
    }

    [Fact]
    public async Task Falls_back_to_mappingProfileId_when_the_node_has_no_natural_key_fields()
    {
        // An older node saved before sourceConnectionId/destinationId were stamped onto it.
        var profileId = Guid.NewGuid();
        var realProfile = new MappingProfile(
            "Patient", "Patient", Guid.NewGuid(), Guid.NewGuid(), "Patient",
            [new MappingField("Active", "$.active", MappingValueType.Boolean, IsRequired: false, DefaultValue: null, Format: "directField")]);

        var repository = new Mock<IConfigurationRepository>();
        repository.Setup(r => r.GetMappingProfileAsync(profileId, It.IsAny<CancellationToken>())).ReturnsAsync(realProfile);

        var executor = new MappingNodeExecutor(
            new FakeJsonMappingEngine(new MappingTestResultDto(new Dictionary<string, object?> { ["Active"] = true }, [])),
            mappingMaterializer: null,
            configurationRepository: repository.Object);
        var node = CreateNode("Patient", "Patient", fields: [], extraConfig: new Dictionary<string, object>
        {
            ["mappingProfileId"] = profileId.ToString(),
        });
        var upstream = UpstreamWith(new ResourceEnvelope("Patient", "p1", """{"resourceType":"Patient"}"""));

        var output = await executor.ExecuteAsync(CreateContext(), node, [upstream], CancellationToken.None);

        var batch = (MappedRecordBatch)output.Payload!;
        ((MappedDestinationRecord)batch.Records.Single()).Values["Active"].Should().Be(true);
        repository.Verify(r => r.GetMappingProfileAsync(profileId, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Regression test for a real production incident: a single Field Mapping node fed by an EpicSource node
    /// configured for BOTH Patient and Observation scopes received a mixed batch, and the executor applied its
    /// one resolved Patient profile to every envelope — Observation JSON got walked against Patient's fields,
    /// matching only the shared root "id", and silently produced a garbage "Patient" row (Observation's id as
    /// PatientId, every real Patient column null). Each resource type must resolve and use its OWN profile.
    /// </summary>
    [Fact]
    public async Task Different_resource_types_in_the_same_batch_are_each_mapped_through_their_own_profile()
    {
        var sourceConnectionId = Guid.NewGuid();
        var destinationId = Guid.NewGuid();
        var patientProfile = new MappingProfile(
            "Patient", "Patient", sourceConnectionId, destinationId, "Patient",
            [
                new MappingField("PatientId", "$.id", MappingValueType.String, IsRequired: false, DefaultValue: null, Format: "directField"),
                new MappingField("Active", "$.active", MappingValueType.Boolean, IsRequired: false, DefaultValue: null, Format: "directField"),
            ]);
        var observationProfile = new MappingProfile(
            "Observation", "Observation", sourceConnectionId, destinationId, "Observation",
            [
                new MappingField("ObservationId", "$.id", MappingValueType.String, IsRequired: false, DefaultValue: null, Format: "directField"),
                new MappingField("Status", "$.status", MappingValueType.String, IsRequired: false, DefaultValue: null, Format: "directField"),
            ]);

        var repository = new Mock<IConfigurationRepository>();
        repository.Setup(r => r.FindMappingProfileAsync("Patient", sourceConnectionId, destinationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(patientProfile);
        repository.Setup(r => r.FindMappingProfileAsync("Observation", sourceConnectionId, destinationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(observationProfile);

        var executor = new MappingNodeExecutor(
            new RoutingFakeJsonMappingEngine(),
            mappingMaterializer: null,
            configurationRepository: repository.Object);
        var node = CreateNode("Patient", "Patient", fields: [], extraConfig: new Dictionary<string, object>
        {
            ["sourceConnectionId"] = sourceConnectionId.ToString(),
            ["destinationId"] = destinationId.ToString(),
        });
        var upstream = UpstreamWith(
            new ResourceEnvelope("Patient", "p1", """{"id":"p1","active":true}"""),
            new ResourceEnvelope("Observation", "o1", """{"id":"o1","status":"final"}"""));

        var output = await executor.ExecuteAsync(CreateContext(), node, [upstream], CancellationToken.None);

        var batch = (MappedRecordBatch)output.Payload!;
        batch.Records.Should().HaveCount(2);
        var records = batch.Records.Cast<MappedDestinationRecord>().ToList();

        var patientRecord = records.Single(r => r.ResourceType == "Patient");
        patientRecord.DestinationObject.Should().Be("Patient");
        patientRecord.Values["PatientId"].Should().Be("p1");
        patientRecord.Values["Active"].Should().Be(true);
        patientRecord.Values.Should().NotContainKey("Status", "Observation's field must never be applied to a Patient record");

        var observationRecord = records.Single(r => r.ResourceType == "Observation");
        observationRecord.DestinationObject.Should().Be("Observation");
        observationRecord.Values["ObservationId"].Should().Be("o1");
        observationRecord.Values["Status"].Should().Be("final");
        observationRecord.Values.Should().NotContainKey("Active", "Patient's field must never be applied to an Observation record");
    }

    [Fact]
    public async Task A_resource_type_with_no_resolvable_profile_is_skipped_not_mapped_through_the_nodes_own_profile()
    {
        var sourceConnectionId = Guid.NewGuid();
        var destinationId = Guid.NewGuid();
        var patientProfile = new MappingProfile(
            "Patient", "Patient", sourceConnectionId, destinationId, "Patient",
            [new MappingField("PatientId", "$.id", MappingValueType.String, IsRequired: false, DefaultValue: null, Format: "directField")]);

        var repository = new Mock<IConfigurationRepository>();
        repository.Setup(r => r.FindMappingProfileAsync("Patient", sourceConnectionId, destinationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(patientProfile);
        repository.Setup(r => r.FindMappingProfileAsync("Observation", sourceConnectionId, destinationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((MappingProfile?)null);

        var executor = new MappingNodeExecutor(
            new RoutingFakeJsonMappingEngine(),
            mappingMaterializer: null,
            configurationRepository: repository.Object);
        // Node's own configured resourceType is "Patient" — the Observation envelope must NOT fall back to it.
        var node = CreateNode("Patient", "Patient", fields: [], extraConfig: new Dictionary<string, object>
        {
            ["sourceConnectionId"] = sourceConnectionId.ToString(),
            ["destinationId"] = destinationId.ToString(),
        });
        var upstream = UpstreamWith(
            new ResourceEnvelope("Patient", "p1", """{"id":"p1"}"""),
            new ResourceEnvelope("Observation", "o1", """{"id":"o1","status":"final"}"""));

        var output = await executor.ExecuteAsync(CreateContext(), node, [upstream], CancellationToken.None);

        var batch = (MappedRecordBatch)output.Payload!;
        batch.Records.Should().HaveCount(1, "the Observation envelope has no resolvable profile and must be skipped, not mapped through Patient's fields");
        ((MappedDestinationRecord)batch.Records.Single()).ResourceType.Should().Be("Patient");
    }

    [Fact]
    public async Task Maps_each_resource_type_using_only_its_own_configured_mappingProfileIds_entry()
    {
        var patientProfileId = Guid.NewGuid();
        var conditionProfileId = Guid.NewGuid();

        var repository = new Mock<IConfigurationRepository>();
        repository.Setup(r => r.GetMappingProfileAsync(patientProfileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(LegacyProfile("Patient", "patients.csv", ("Id", "$.id"), ("NameFamily", "$.name[*].family")));
        repository.Setup(r => r.GetMappingProfileAsync(conditionProfileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(LegacyProfile("Condition", "conditions.csv", ("ConditionId", "$.id"), ("Code", "$.code.coding[*].display")));

        var node = BuildLegacyNode(JsonSerializer.Serialize(new
        {
            mappingProfileIds = new Dictionary<string, string>
            {
                ["Patient"] = patientProfileId.ToString(),
                ["Condition"] = conditionProfileId.ToString(),
            },
        }));
        var executor = new MappingNodeExecutor(EchoFieldsEngine().Object, configurationRepository: repository.Object);
        var context = CreateContext();

        var inputs = new[]
        {
            SourceOutput(
                new ResourceEnvelope("Patient", "p1", "{}"),
                new ResourceEnvelope("Condition", "c1", "{}"),
                new ResourceEnvelope("Observation", "o1", "{}")), // no profile configured for this one
        };

        var output = await executor.ExecuteAsync(context, node, inputs, CancellationToken.None);

        var records = output.Payload.Should().BeOfType<MappedRecordBatch>().Subject.Records
            .OfType<MappedDestinationRecord>().ToList();

        records.Should().HaveCount(2); // Observation dropped — no mapping configured for it on this node

        var patientRecord = records.Single(r => r.ResourceType == "Patient");
        patientRecord.DestinationObject.Should().Be("patients.csv");
        patientRecord.Values.Keys.Should().BeEquivalentTo(["Id", "NameFamily"]);

        var conditionRecord = records.Single(r => r.ResourceType == "Condition");
        conditionRecord.DestinationObject.Should().Be("conditions.csv");
        conditionRecord.Values.Keys.Should().BeEquivalentTo(["ConditionId", "Code"]);
    }

    [Fact]
    public async Task Legacy_single_mappingProfileId_still_resolves_but_no_longer_leaks_onto_other_resource_types()
    {
        var profileId = Guid.NewGuid();
        var repository = new Mock<IConfigurationRepository>();
        repository.Setup(r => r.GetMappingProfileAsync(profileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(LegacyProfile("Patient", "patients.csv", ("Id", "$.id"), ("NameFamily", "$.name[*].family")));

        var node = BuildLegacyNode($$"""{"mappingProfileId":"{{profileId}}"}""");
        var executor = new MappingNodeExecutor(EchoFieldsEngine().Object, configurationRepository: repository.Object);
        var context = CreateContext();

        var inputs = new[]
        {
            SourceOutput(
                new ResourceEnvelope("Patient", "p1", "{}"),
                new ResourceEnvelope("Observation", "o1", "{}")),
        };

        var output = await executor.ExecuteAsync(context, node, inputs, CancellationToken.None);
        var records = output.Payload.Should().BeOfType<MappedRecordBatch>().Subject.Records
            .OfType<MappedDestinationRecord>().ToList();

        // Only the Patient record survives — pre-fix, the Observation record would have been mapped with Patient's
        // own fields too (NameFamily etc. empty, since Observation JSON has no such path).
        records.Should().ContainSingle();
        records[0].ResourceType.Should().Be("Patient");
    }

    [Fact]
    public async Task No_repository_composed_falls_back_to_inline_config_for_a_single_resource_type()
    {
        var node = BuildLegacyNode(JsonSerializer.Serialize(new
        {
            resourceType = "Patient",
            destinationObject = "patients.csv",
            fields = new[] { new MappingFieldDto("Id", "$.id", MappingValueType.String, false, null, null) },
        }));
        var executor = new MappingNodeExecutor(EchoFieldsEngine().Object, configurationRepository: null);
        var context = CreateContext();

        var inputs = new[]
        {
            SourceOutput(
                new ResourceEnvelope("Patient", "p1", "{}"),
                new ResourceEnvelope("Observation", "o1", "{}")),
        };

        var output = await executor.ExecuteAsync(context, node, inputs, CancellationToken.None);
        var records = output.Payload.Should().BeOfType<MappedRecordBatch>().Subject.Records
            .OfType<MappedDestinationRecord>().ToList();

        records.Should().ContainSingle();
        records[0].ResourceType.Should().Be("Patient");
        records[0].DestinationObject.Should().Be("patients.csv");
    }

    /// <summary>
    /// End-to-end proof that the transform-rule engine is actually wired into this executor: when a
    /// destination and the rule dependencies are supplied, a resolved rule is applied to the mapped value
    /// before it lands in the record — the whole point of this wiring (see FHIRBridge.Application.Services
    /// .Transforms). Uses a REAL TransformNodeRegistry/StringNormalizationNode (not a fake) so this proves the
    /// actual node execution path, not just that a mock was called.
    /// </summary>
    [Fact]
    public async Task Applies_a_resolved_transform_rule_to_the_mapped_value_before_it_is_written()
    {
        var destination = new DestinationConfiguration(
            "Test SQL", DestinationType.SqlServer, new SecretReference("kv", "secret"), "FHIRBridge");
        var destinationId = destination.Id;

        var fields = new[]
        {
            new MappingFieldDto("FamilyName", "$.name.family", MappingValueType.String, IsRequired: false,
                DefaultValue: null, Format: "directField", ResourceType: "Patient", DestinationObject: "Patient"),
        };
        var engine = new FakeJsonMappingEngine(new MappingTestResultDto(
            Values: new Dictionary<string, object?> { ["FamilyName"] = "roe" }, Errors: []));

        var repository = new Mock<IConfigurationRepository>();
        repository.Setup(r => r.GetDestinationAsync(destinationId, It.IsAny<CancellationToken>())).ReturnsAsync(destination);

        var rule = new TransformationRule(
            TransformScope.Field, TransformNodeType.StringNormalization,
            JsonSerializer.Serialize(new Dictionary<string, string> { ["case"] = "upper" }),
            resourceType: "Patient", destinationField: "FamilyName");
        var resolver = new Mock<IEffectiveRuleResolver>();
        resolver
            .Setup(r => r.ResolveAsync(
                DestinationType.SqlServer, "Patient", "FamilyName", It.IsAny<Guid>(), null, "$.name.family", It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[rule]);

        var registry = new TransformNodeRegistry([new StringNormalizationNode()]);
        var settingsCache = new Mock<ISystemSettingsCache>();
        settingsCache
            .Setup(c => c.GetBoolAsync(TransformationRulesFeatureFlag.SettingKey, TransformationRulesFeatureFlag.DefaultHidden, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var executor = new MappingNodeExecutor(
            engine, mappingMaterializer: null, configurationRepository: repository.Object,
            ruleResolver: resolver.Object, transformNodeRegistry: registry, settingsCache: settingsCache.Object);
        var node = CreateNode("Patient", "Patient", fields, extraConfig: new Dictionary<string, object>
        {
            ["destinationId"] = destinationId.ToString(),
        });
        var upstream = UpstreamWith(new ResourceEnvelope("Patient", "p1", """{"resourceType":"Patient"}"""));

        var output = await executor.ExecuteAsync(CreateContext(), node, [upstream], CancellationToken.None);

        var batch = (MappedRecordBatch)output.Payload!;
        var record = (MappedDestinationRecord)batch.Records.Single();
        record.Values["FamilyName"].Should().Be("ROE", "the resolved StringNormalization rule (case=upper) must run before the value is written");
    }

    /// <summary>
    /// The feature-flag gate (Settings &gt; System Settings &gt; General, "TransformationRules:Hidden",
    /// default true): even with a fully wired rule resolver/registry and a resolvable rule, the executor must
    /// skip applying it while the flag reads hidden — this is what lets the whole rules feature ship dark by
    /// default without touching any pipeline behavior.
    /// </summary>
    [Fact]
    public async Task Suppresses_rule_application_when_the_feature_flag_reads_hidden()
    {
        var destination = new DestinationConfiguration(
            "Test SQL", DestinationType.SqlServer, new SecretReference("kv", "secret"), "FHIRBridge");
        var destinationId = destination.Id;

        var fields = new[]
        {
            new MappingFieldDto("FamilyName", "$.name.family", MappingValueType.String, IsRequired: false,
                DefaultValue: null, Format: "directField", ResourceType: "Patient", DestinationObject: "Patient"),
        };
        var engine = new FakeJsonMappingEngine(new MappingTestResultDto(
            Values: new Dictionary<string, object?> { ["FamilyName"] = "roe" }, Errors: []));

        var repository = new Mock<IConfigurationRepository>();
        repository.Setup(r => r.GetDestinationAsync(destinationId, It.IsAny<CancellationToken>())).ReturnsAsync(destination);

        var rule = new TransformationRule(
            TransformScope.Field, TransformNodeType.StringNormalization,
            JsonSerializer.Serialize(new Dictionary<string, string> { ["case"] = "upper" }),
            resourceType: "Patient", destinationField: "FamilyName");
        var resolver = new Mock<IEffectiveRuleResolver>();
        resolver
            .Setup(r => r.ResolveAsync(
                DestinationType.SqlServer, "Patient", "FamilyName", It.IsAny<Guid>(), null, "$.name.family", It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[rule]);

        var registry = new TransformNodeRegistry([new StringNormalizationNode()]);
        var settingsCache = new Mock<ISystemSettingsCache>();
        settingsCache
            .Setup(c => c.GetBoolAsync(TransformationRulesFeatureFlag.SettingKey, TransformationRulesFeatureFlag.DefaultHidden, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var executor = new MappingNodeExecutor(
            engine, mappingMaterializer: null, configurationRepository: repository.Object,
            ruleResolver: resolver.Object, transformNodeRegistry: registry, settingsCache: settingsCache.Object);
        var node = CreateNode("Patient", "Patient", fields, extraConfig: new Dictionary<string, object>
        {
            ["destinationId"] = destinationId.ToString(),
        });
        var upstream = UpstreamWith(new ResourceEnvelope("Patient", "p1", """{"resourceType":"Patient"}"""));

        var output = await executor.ExecuteAsync(CreateContext(), node, [upstream], CancellationToken.None);

        var batch = (MappedRecordBatch)output.Payload!;
        var record = (MappedDestinationRecord)batch.Records.Single();
        record.Values["FamilyName"].Should().Be("roe", "the flag is hidden, so the resolved rule must not be applied");
    }

    [Fact]
    public async Task Is_a_no_op_when_no_rule_dependencies_are_supplied_existing_behavior_unchanged()
    {
        var fields = new[]
        {
            new MappingFieldDto("FamilyName", "$.name.family", MappingValueType.String, IsRequired: false,
                DefaultValue: null, Format: "directField", ResourceType: "Patient", DestinationObject: "Patient"),
        };
        var engine = new FakeJsonMappingEngine(new MappingTestResultDto(
            Values: new Dictionary<string, object?> { ["FamilyName"] = "roe" }, Errors: []));
        // No configurationRepository, no ruleResolver, no transformNodeRegistry — the optional-dependency path.
        var executor = new MappingNodeExecutor(engine, mappingMaterializer: null, configurationRepository: null);
        var node = CreateNode("Patient", "Patient", fields);
        var upstream = UpstreamWith(new ResourceEnvelope("Patient", "p1", """{"resourceType":"Patient"}"""));

        var output = await executor.ExecuteAsync(CreateContext(), node, [upstream], CancellationToken.None);

        var batch = (MappedRecordBatch)output.Payload!;
        ((MappedDestinationRecord)batch.Records.Single()).Values["FamilyName"].Should().Be("roe");
    }

    private static WorkflowNode CreateNode(
        string resourceType, string destinationObject, IReadOnlyCollection<MappingFieldDto> fields,
        IReadOnlyDictionary<string, object>? extraConfig = null)
    {
        var config = new Dictionary<string, object>
        {
            ["resourceType"] = resourceType,
            ["destinationObject"] = destinationObject,
            ["fields"] = fields,
        };
        foreach (var (key, value) in extraConfig ?? new Dictionary<string, object>())
        {
            config[key] = value;
        }

        var workflow = new WorkflowDefinition(Guid.NewGuid(), "mapping-node-test", 1);
        return workflow.AddNode(
            WorkflowNodeTypes.Mapping,
            WorkflowNodeCategory.Transform,
            60,
            configurationJson: JsonSerializer.Serialize(config, JsonOptions));
    }

    private static WorkflowNode BuildLegacyNode(string configurationJson)
    {
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "mapping-test", 1);
        return workflow.AddNode(WorkflowNodeTypes.Mapping, WorkflowNodeCategory.Transform, rank: 50, configurationJson: configurationJson);
    }

    private static WorkflowNodeOutput UpstreamWith(params ResourceEnvelope[] resources) =>
        new(Guid.NewGuid(), WorkflowNodeTypes.EpicSource, new ResourceBatch(resources), WorkflowDataContract.ResourceBatch);

    private static WorkflowNodeOutput SourceOutput(params ResourceEnvelope[] resources) =>
        new(Guid.NewGuid(), WorkflowNodeTypes.EpicSource, new ResourceBatch(resources), WorkflowDataContract.ResourceBatch);

    private static WorkflowExecutionContext CreateContext() => new(Guid.NewGuid(), "test-correlation");

    // Echoes the profile's own fields back as Values (TargetField -> its JsonPath) regardless of source JSON — lets
    // assertions prove exactly which fields were applied to which resource, without a real JSONPath engine.
    private static Mock<IJsonMappingEngine> EchoFieldsEngine()
    {
        var engine = new Mock<IJsonMappingEngine>();
        engine
            .Setup(e => e.Map(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<MappingFieldDto>>(), It.IsAny<IReadOnlyDictionary<string, object?>?>()))
            .Returns((string _, IReadOnlyCollection<MappingFieldDto> fields, IReadOnlyDictionary<string, object?>? _) =>
                new MappingTestResultDto(
                    fields.ToDictionary(f => f.TargetField, object? (f) => f.JsonPath),
                    []));
        return engine;
    }

    private static MappingProfile LegacyProfile(string resourceType, string destinationObject, params (string Target, string JsonPath)[] fields) =>
        new(
            $"{resourceType} mapping",
            resourceType,
            Guid.NewGuid(),
            Guid.NewGuid(),
            destinationObject,
            fields.Select(f => new MappingField(f.Target, f.JsonPath, MappingValueType.String, false, null, null)));

    private sealed class FakeJsonMappingEngine : IJsonMappingEngine
    {
        private readonly MappingTestResultDto _result;
        public FakeJsonMappingEngine(MappingTestResultDto result) => _result = result;
        public MappingTestResultDto Map(
            string sourceJson, IReadOnlyCollection<MappingFieldDto> fields, IReadOnlyDictionary<string, object?>? systemValues = null) => _result;
    }

    /// <summary>A minimal real mapper (unlike <see cref="FakeJsonMappingEngine"/>'s fixed canned result) that
    /// resolves each field's simple root-level "$.property" JsonPath against the actual source JSON — needed to
    /// prove that different resource types in one batch really do get mapped through their OWN distinct fields,
    /// not a fixed stand-in result that couldn't tell the two apart.</summary>
    private sealed class RoutingFakeJsonMappingEngine : IJsonMappingEngine
    {
        public MappingTestResultDto Map(
            string sourceJson, IReadOnlyCollection<MappingFieldDto> fields, IReadOnlyDictionary<string, object?>? systemValues = null)
        {
            using var document = JsonDocument.Parse(sourceJson);
            var values = new Dictionary<string, object?>();
            foreach (var field in fields)
            {
                var propertyName = field.JsonPath.TrimStart('$', '.');
                if (!document.RootElement.TryGetProperty(propertyName, out var element))
                {
                    continue;
                }

                values[field.TargetField] = element.ValueKind switch
                {
                    JsonValueKind.String => element.GetString(),
                    JsonValueKind.True or JsonValueKind.False => element.GetBoolean(),
                    _ => null
                };
            }

            return new MappingTestResultDto(values, []);
        }
    }
}
