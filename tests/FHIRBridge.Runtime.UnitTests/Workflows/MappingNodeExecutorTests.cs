using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Mapping;
using FHIRBridge.Application.DTOs;
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

// Covers the multi-resource mapping fix: a destination selecting more than one resource (Patient + Observation +
// Condition) previously had only one MappingProfile wired to its single MappingNode — every OTHER resource type
// silently got that one profile's fields applied to it too (only "Id" ever matched, since it's the one property
// every FHIR resource happens to share; everything else came back empty). MappingNodeExecutor must now resolve one
// profile per resource type and apply only the matching one, skipping any resource type with no configured mapping.
public sealed class MappingNodeExecutorTests
{
    private static WorkflowNode BuildNode(string configurationJson)
    {
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "mapping-test", 1);
        return workflow.AddNode(WorkflowNodeTypes.Mapping, WorkflowNodeCategory.Transform, rank: 50, configurationJson: configurationJson);
    }

    private static WorkflowNodeOutput SourceOutput(params ResourceEnvelope[] resources) =>
        new(Guid.NewGuid(), WorkflowNodeTypes.EpicSource, new ResourceBatch(resources), WorkflowDataContract.ResourceBatch);

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

    private static MappingProfile Profile(string resourceType, string destinationObject, params (string Target, string JsonPath)[] fields) =>
        new(
            $"{resourceType} mapping",
            resourceType,
            Guid.NewGuid(),
            Guid.NewGuid(),
            destinationObject,
            fields.Select(f => new MappingField(f.Target, f.JsonPath, MappingValueType.String, false, null, null)));

    [Fact]
    public async Task Maps_each_resource_type_using_only_its_own_configured_profile()
    {
        var patientProfileId = Guid.NewGuid();
        var conditionProfileId = Guid.NewGuid();

        var repository = new Mock<IConfigurationRepository>();
        repository.Setup(r => r.GetMappingProfileAsync(patientProfileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Profile("Patient", "patients.csv", ("Id", "$.id"), ("NameFamily", "$.name[*].family")));
        repository.Setup(r => r.GetMappingProfileAsync(conditionProfileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Profile("Condition", "conditions.csv", ("ConditionId", "$.id"), ("Code", "$.code.coding[*].display")));

        var node = BuildNode(System.Text.Json.JsonSerializer.Serialize(new
        {
            mappingProfileIds = new Dictionary<string, string>
            {
                ["Patient"] = patientProfileId.ToString(),
                ["Condition"] = conditionProfileId.ToString(),
            },
        }));
        var executor = new MappingNodeExecutor(EchoFieldsEngine().Object, repository.Object);
        var context = new WorkflowExecutionContext(Guid.NewGuid(), "corr");

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
            .ReturnsAsync(Profile("Patient", "patients.csv", ("Id", "$.id"), ("NameFamily", "$.name[*].family")));

        var node = BuildNode($$"""{"mappingProfileId":"{{profileId}}"}""");
        var executor = new MappingNodeExecutor(EchoFieldsEngine().Object, repository.Object);
        var context = new WorkflowExecutionContext(Guid.NewGuid(), "corr");

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
        var node = BuildNode(System.Text.Json.JsonSerializer.Serialize(new
        {
            resourceType = "Patient",
            destinationObject = "patients.csv",
            fields = new[] { new MappingFieldDto("Id", "$.id", MappingValueType.String, false, null, null) },
        }));
        var executor = new MappingNodeExecutor(EchoFieldsEngine().Object, configurationRepository: null);
        var context = new WorkflowExecutionContext(Guid.NewGuid(), "corr");

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

    // FHIR-repository passthrough (e.g. Aidbox): when this node's config carries destinationTransformId=dest-fhir
    // (stamped by WorkflowGraphMapperService.syntheticMappingRequest, which copies the destination node's own
    // fields onto the synthetic Field Mapping node) and dest_fhirMapMode isn't "customize", every resource must be
    // emitted unchanged — no configured mapping profile required at all. Before this fix, a resource type with no
    // configured field-mapping profile was silently dropped (see the three tests above), which meant a passthrough
    // FHIR destination always received zero records and wrote nothing, while the workflow run still reported
    // "Succeeded" (confirmed against a real Aidbox instance).
    [Fact]
    public async Task FhirRepository_passthrough_emits_every_resource_unchanged_with_no_mapping_profile_configured()
    {
        var node = BuildNode(System.Text.Json.JsonSerializer.Serialize(new
        {
            destinationTransformId = "dest-fhir",
        }));
        var executor = new MappingNodeExecutor(EchoFieldsEngine().Object, configurationRepository: null);
        var context = new WorkflowExecutionContext(Guid.NewGuid(), "corr");

        var patientJson = """{"resourceType":"Patient","id":"1005","name":[{"family":"Iyer","given":["Divya"]}]}""";
        var observationJson = """{"resourceType":"Observation","id":"obs-1"}""";
        var inputs = new[]
        {
            SourceOutput(
                new ResourceEnvelope("Patient", "1005", patientJson),
                new ResourceEnvelope("Observation", "obs-1", observationJson)),
        };

        var output = await executor.ExecuteAsync(context, node, inputs, CancellationToken.None);
        var records = output.Payload.Should().BeOfType<MappedRecordBatch>().Subject.Records
            .OfType<MappedDestinationRecord>().ToList();

        records.Should().HaveCount(2);
        var patientRecord = records.Single(r => r.ResourceType == "Patient");
        patientRecord.SourceJson.Should().Be(patientJson);
        patientRecord.Values.Should().BeEmpty();
        patientRecord.SourceResourceId.Should().Be("1005");

        var observationRecord = records.Single(r => r.ResourceType == "Observation");
        observationRecord.SourceJson.Should().Be(observationJson);
        observationRecord.Values.Should().BeEmpty();
    }

    [Fact]
    public async Task FhirRepository_explicit_passthrough_mode_behaves_identically_to_absent_dest_fhirMapMode()
    {
        var node = BuildNode(System.Text.Json.JsonSerializer.Serialize(new
        {
            destinationTransformId = "dest-fhir",
            dest_fhirMapMode = "passthrough",
        }));
        var executor = new MappingNodeExecutor(EchoFieldsEngine().Object, configurationRepository: null);
        var context = new WorkflowExecutionContext(Guid.NewGuid(), "corr");

        var patientJson = """{"resourceType":"Patient","id":"1005"}""";
        var inputs = new[] { SourceOutput(new ResourceEnvelope("Patient", "1005", patientJson)) };

        var output = await executor.ExecuteAsync(context, node, inputs, CancellationToken.None);
        var records = output.Payload.Should().BeOfType<MappedRecordBatch>().Subject.Records
            .OfType<MappedDestinationRecord>().ToList();

        records.Should().ContainSingle();
        records[0].SourceJson.Should().Be(patientJson);
    }

    [Fact]
    public async Task FhirRepository_customize_mode_with_no_rules_configured_emits_every_resource_unchanged()
    {
        // No dest_fhirCustomRules at all (or none scoped to this resource type) — customize mode still takes its
        // own passthrough-shaped branch (not the field-mapping engine below), it just has zero rules to apply.
        var node = BuildNode(System.Text.Json.JsonSerializer.Serialize(new
        {
            destinationTransformId = "dest-fhir",
            dest_fhirMapMode = "customize",
        }));
        var executor = new MappingNodeExecutor(EchoFieldsEngine().Object, configurationRepository: null);
        var context = new WorkflowExecutionContext(Guid.NewGuid(), "corr");

        var patientJson = """{"resourceType":"Patient","id":"1005"}""";
        var inputs = new[] { SourceOutput(new ResourceEnvelope("Patient", "1005", patientJson)) };

        var output = await executor.ExecuteAsync(context, node, inputs, CancellationToken.None);
        var records = output.Payload.Should().BeOfType<MappedRecordBatch>().Subject.Records
            .OfType<MappedDestinationRecord>().ToList();

        records.Should().ContainSingle();
        records[0].SourceJson.Should().Be(patientJson);
        output.Metadata!["customize"].Should().Be(true);
    }

    [Fact]
    public async Task FhirRepository_customize_mode_applies_rules_scoped_to_the_matching_resource_type_only()
    {
        var rules = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["Patient"] = new[]
            {
                new { field = "active", transform = "booleanConversion", @params = new { trueValues = "Y" } },
            },
            // Scoped to a resource type not present in this batch — must have no effect on Observation below.
            ["Observation"] = new[]
            {
                new { field = "status", transform = "typeCast", @params = new { targetType = "integer" } },
            },
        });
        var node = BuildNode(System.Text.Json.JsonSerializer.Serialize(new
        {
            destinationTransformId = "dest-fhir",
            dest_fhirMapMode = "customize",
            dest_fhirCustomRules = rules,
        }));
        var executor = new MappingNodeExecutor(EchoFieldsEngine().Object, configurationRepository: null);
        var context = new WorkflowExecutionContext(Guid.NewGuid(), "corr");

        var patientJson = """{"resourceType":"Patient","id":"1005","active":"Y"}""";
        var observationJson = """{"resourceType":"Observation","id":"obs-1","status":"final"}""";
        var inputs = new[]
        {
            SourceOutput(
                new ResourceEnvelope("Patient", "1005", patientJson),
                new ResourceEnvelope("Observation", "obs-1", observationJson)),
        };

        var output = await executor.ExecuteAsync(context, node, inputs, CancellationToken.None);
        var records = output.Payload.Should().BeOfType<MappedRecordBatch>().Subject.Records
            .OfType<MappedDestinationRecord>().ToList();

        var patientRecord = records.Single(r => r.ResourceType == "Patient");
        patientRecord.SourceJson.Should().Contain("\"active\":true");

        // Observation's rule never applies here (it's scoped to a resource type absent from this batch) —
        // unchanged, proving rules are matched by the resource's own type, not applied globally.
        var observationRecord = records.Single(r => r.ResourceType == "Observation");
        observationRecord.SourceJson.Should().Be(observationJson);
    }

    [Fact]
    public async Task FhirRepository_customize_mode_isolates_a_failing_rule_reports_it_but_still_emits_the_resource()
    {
        var rules = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["Patient"] = new[]
            {
                new { field = "birthDate", transform = "dateFormat", @params = new { } },
            },
        });
        var node = BuildNode(System.Text.Json.JsonSerializer.Serialize(new
        {
            destinationTransformId = "dest-fhir",
            dest_fhirMapMode = "customize",
            dest_fhirCustomRules = rules,
        }));
        var executor = new MappingNodeExecutor(EchoFieldsEngine().Object, configurationRepository: null);
        var context = new WorkflowExecutionContext(Guid.NewGuid(), "corr");

        // birthDate is not a parseable date — the rule must fail in isolation, not throw out of ExecuteAsync.
        var patientJson = """{"resourceType":"Patient","id":"1005","birthDate":"not-a-date"}""";
        var inputs = new[] { SourceOutput(new ResourceEnvelope("Patient", "1005", patientJson)) };

        var output = await executor.ExecuteAsync(context, node, inputs, CancellationToken.None);
        var records = output.Payload.Should().BeOfType<MappedRecordBatch>().Subject.Records
            .OfType<MappedDestinationRecord>().ToList();

        records.Should().ContainSingle();
        records[0].SourceJson.Should().Be(patientJson); // unmodified — the failed rule left it as-is
        var ruleErrors = output.Metadata!["ruleErrors"].Should().BeAssignableTo<List<string>>().Subject;
        ruleErrors.Should().ContainSingle(e => e.Contains("Patient.birthDate") && e.Contains("dateFormat"));
    }

    [Fact]
    public async Task Non_fhir_destination_is_unaffected_by_the_passthrough_shortcut()
    {
        // Regression guard: a destinationTransformId that isn't "dest-fhir" (e.g. a SQL Server destination node's
        // own fields, also spread onto the synthetic node) must never take the passthrough shortcut, even though
        // this node's config now carries that key too.
        var node = BuildNode(System.Text.Json.JsonSerializer.Serialize(new
        {
            destinationTransformId = "dest-sqlserver",
            resourceType = "Patient",
            destinationObject = "patients",
            fields = new[] { new MappingFieldDto("Id", "$.id", MappingValueType.String, false, null, null) },
        }));
        var executor = new MappingNodeExecutor(EchoFieldsEngine().Object, configurationRepository: null);
        var context = new WorkflowExecutionContext(Guid.NewGuid(), "corr");

        var inputs = new[] { SourceOutput(new ResourceEnvelope("Patient", "p1", "{}")) };

        var output = await executor.ExecuteAsync(context, node, inputs, CancellationToken.None);
        var records = output.Payload.Should().BeOfType<MappedRecordBatch>().Subject.Records
            .OfType<MappedDestinationRecord>().ToList();

        records.Should().ContainSingle();
        records[0].Values.Keys.Should().BeEquivalentTo(["Id"]); // real field mapping ran, not passthrough
    }
}
