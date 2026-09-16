using System.Text.Json;
using FHIRBridge.Application.Abstractions.Mapping;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Application.Workflows.Catalog;
using FHIRBridge.Runtime.Application.Workflows.Payloads;
using FHIRBridge.Runtime.Domain.Workflows;
using FHIRBridge.Runtime.Infrastructure.Workflows.Executors;
using FluentAssertions;
using Moq;
using Xunit;
using ResourceEnvelope = FHIRBridge.Runtime.Application.Workflows.Payloads.ResourceEnvelope;

namespace FHIRBridge.Runtime.UnitTests.Workflows;

/// <summary>
/// Phase 5 of the self-contained plan (§3.3): a node carrying its own inline mapping is authoritative, and the
/// master-record lookup is only a fallback for nodes not yet migrated. This is what makes a run reproducible —
/// what ran is what the node says — and stops an edit to a shared profile silently changing this workflow.
/// </summary>
public sealed class InlineMappingPrecedenceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static MappingFieldDto Field(string target, string jsonPath) =>
        new(target, jsonPath, MappingValueType.String, IsRequired: false, DefaultValue: null, Format: null);

    private static WorkflowNode NodeWith(Dictionary<string, object> config)
    {
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "inline-mapping-test", 1);
        return workflow.AddNode(
            WorkflowNodeTypes.Mapping,
            WorkflowNodeCategory.Transform,
            60,
            configurationJson: JsonSerializer.Serialize(config, JsonOptions));
    }

    /// <summary>A repository whose profile is deliberately DIFFERENT from the inline mapping, so any test that
    /// sees these columns proves the executor went to the master record instead of the node.</summary>
    private static Mock<IConfigurationRepository> RepositoryWithProfile(Guid profileId)
    {
        var profile = new MappingProfile(
            "Master profile",
            "Patient",
            Guid.NewGuid(),
            Guid.NewGuid(),
            "dbo.FromMaster",
            [new MappingField("FromMaster", "$.id", MappingValueType.String, false, null, null)]);

        var repository = new Mock<IConfigurationRepository>();
        repository
            .Setup(r => r.GetMappingProfileAsync(profileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(profile);
        return repository;
    }

    private static async Task<MappedDestinationRecord> RunAsync(
        WorkflowNode node,
        IConfigurationRepository? repository)
    {
        var executor = new MappingNodeExecutor(
            new JsonMappingEngine(), mappingMaterializer: null, configurationRepository: repository);

        var upstream = new WorkflowNodeOutput(
            Guid.NewGuid(),
            WorkflowNodeTypes.EpicSource,
            new ResourceBatch([new ResourceEnvelope("Patient", "p1", """{"resourceType":"Patient","id":"p1"}""")]),
            WorkflowDataContract.ResourceBatch);

        var output = await executor.ExecuteAsync(
            new WorkflowExecutionContext(Guid.NewGuid(), "corr-1"), node, [upstream], CancellationToken.None);

        var batch = (MappedRecordBatch)output.Payload!;
        return (MappedDestinationRecord)batch.Records.Single();
    }

    [Fact]
    public async Task An_inline_mapping_is_used_without_consulting_the_repository()
    {
        var profileId = Guid.NewGuid();
        var repository = RepositoryWithProfile(profileId);

        var node = NodeWith(new Dictionary<string, object>
        {
            ["resourceType"] = "Patient",
            // The id is still present, exactly as an un-migrated node would carry it...
            ["mappingProfileIds"] = new Dictionary<string, string> { ["Patient"] = profileId.ToString() },
            // ...but the inline mapping is authoritative and must win.
            ["mappings"] = new Dictionary<string, object>
            {
                ["Patient"] = new
                {
                    destinationObject = "dbo.FromNode",
                    fields = new[] { Field("FromNode", "$.id") },
                },
            },
        });

        var record = await RunAsync(node, repository.Object);

        record.DestinationObject.Should().Be("dbo.FromNode");
        record.Values.Should().ContainKey("FromNode");
        record.Values.Should().NotContainKey("FromMaster");

        repository.Verify(
            r => r.GetMappingProfileAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a self-contained node must not need the master record at all");
    }

    [Fact]
    public async Task A_node_with_no_inline_mapping_still_resolves_by_id()
    {
        var profileId = Guid.NewGuid();
        var repository = RepositoryWithProfile(profileId);

        var node = NodeWith(new Dictionary<string, object>
        {
            ["resourceType"] = "Patient",
            ["mappingProfileIds"] = new Dictionary<string, string> { ["Patient"] = profileId.ToString() },
        });

        var record = await RunAsync(node, repository.Object);

        record.DestinationObject.Should().Be("dbo.FromMaster");
        record.Values.Should().ContainKey("FromMaster");
    }

    /// <summary>Resource types are matched case-insensitively everywhere else in the engine; a JSON dictionary
    /// is not, so the inline lookup must not depend on the stored casing.</summary>
    [Fact]
    public async Task Inline_resource_types_match_case_insensitively()
    {
        var node = NodeWith(new Dictionary<string, object>
        {
            ["resourceType"] = "Patient",
            ["mappings"] = new Dictionary<string, object>
            {
                ["patient"] = new
                {
                    destinationObject = "dbo.FromNode",
                    fields = new[] { Field("FromNode", "$.id") },
                },
            },
        });

        var record = await RunAsync(node, repository: null);

        record.DestinationObject.Should().Be("dbo.FromNode");
    }

    /// <summary>An empty or absent inline block must fall through rather than resolving to "no fields", which
    /// would silently write nothing for that resource type.</summary>
    [Fact]
    public async Task An_empty_inline_block_falls_through_to_the_id_lookup()
    {
        var profileId = Guid.NewGuid();
        var repository = RepositoryWithProfile(profileId);

        var node = NodeWith(new Dictionary<string, object>
        {
            ["resourceType"] = "Patient",
            ["mappingProfileIds"] = new Dictionary<string, string> { ["Patient"] = profileId.ToString() },
            ["mappings"] = new Dictionary<string, object>(),
        });

        var record = await RunAsync(node, repository.Object);

        record.DestinationObject.Should().Be("dbo.FromMaster");
    }

    [Fact]
    public async Task An_inline_mapping_for_a_different_resource_type_does_not_apply()
    {
        var profileId = Guid.NewGuid();
        var repository = RepositoryWithProfile(profileId);

        var node = NodeWith(new Dictionary<string, object>
        {
            ["resourceType"] = "Patient",
            ["mappingProfileIds"] = new Dictionary<string, string> { ["Patient"] = profileId.ToString() },
            ["mappings"] = new Dictionary<string, object>
            {
                ["Observation"] = new
                {
                    destinationObject = "dbo.Observation",
                    fields = new[] { Field("ObsValue", "$.valueString") },
                },
            },
        });

        var record = await RunAsync(node, repository.Object);

        record.DestinationObject.Should().Be("dbo.FromMaster");
    }
}
