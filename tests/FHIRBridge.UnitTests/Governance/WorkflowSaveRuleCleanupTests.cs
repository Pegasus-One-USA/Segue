using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Persistence;
using FHIRBridge.Runtime.Application.Workflows.Catalog;
using FHIRBridge.Runtime.Application.Workflows.Storage;
using FHIRBridge.Runtime.Domain.Workflows;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace FHIRBridge.UnitTests.Governance;

/// <summary>
/// The rules a removed Transformation / De-identification step stood for are deleted by the workflow SAVE that
/// drops the step — not when the step is deleted on the canvas. So an unsaved canvas, a step put back before
/// saving, or a save that fails (the cleanup runs inside the save's transaction, see WorkflowEndpoints) leaves the
/// stored workflow and its rules consistent.
///
/// Runs the real cleanup service over the real EF repositories (in-memory provider). The workflow definition
/// this save stores is added to the context first, as the save does before its cleanup pass.
/// </summary>
public sealed class WorkflowSaveRuleCleanupTests : IDisposable
{
    private readonly FHIRBridgeDbContext _db = new(
        new DbContextOptionsBuilder<FHIRBridgeDbContext>()
            // A unique name isolates each test without an EF service provider of its own per test.
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private readonly Guid _workflowId = Guid.NewGuid();

    public void Dispose() => _db.Dispose();

    private Task SaveAsync()
    {
        foreach (var entry in _db.ChangeTracker.Entries().Where(entry => entry.State == EntityState.Added))
        {
            var createdBy = entry.Properties.FirstOrDefault(property => property.Metadata.Name == "CreatedBy");
            if (createdBy is { CurrentValue: null })
            {
                createdBy.CurrentValue = "test";
            }
        }

        return _db.SaveChangesAsync();
    }

    private WorkflowConfigurationCleanupService Cleanup()
    {
        var rules = new EfTransformationRuleRepository(_db);
        return new WorkflowConfigurationCleanupService(
            new EfConfigurationRepository(_db), rules,
            new InMemoryWorkflowDefinitionStore(TestHelpers.LicenseTestScopeFactory.Create()),
            new DeIdentificationProfileService(
                new EfDeIdentificationProfileRepository(_db), new Mock<IDeIdentificationService>().Object, rules));
    }

    /// <summary>A destination connection, this workflow's policy (named with its id) holding one rule, and one
    /// Workflow-scoped transformation rule for that destination. The connection names the policy, as the V2 wizard
    /// leaves it.</summary>
    private async Task<(DestinationConfiguration Destination, DeIdentificationProfile Policy)> SeedAsync()
    {
        var destination = new DestinationConfiguration(
            "Warehouse", DestinationType.SqlServer, new SecretReference("kv", "secret"), "FHIRBridge");
        var policy = new DeIdentificationProfile(_workflowId.ToString());
        destination.SetDeIdentificationProfile(policy.Id);
        _db.DestinationConfigurations.Add(destination);
        _db.DeIdentificationProfiles.Add(policy);
        _db.TransformationRules.Add(new TransformationRule(
            TransformScope.ResourceType, TransformNodeType.HashingMasking, """{"mode":"mask","keepLength":"4"}""",
            resourceType: "Patient", sourceField: "$.name[*].given[*]", executionPhase: TransformExecutionPhase.PreMapping,
            deIdentificationProfileId: policy.Id));
        _db.TransformationRules.Add(new TransformationRule(
            TransformScope.Workflow, TransformNodeType.ConcatenationTemplating, """{"template":"{0} {1}"}""",
            destinationType: DestinationType.SqlServer, resourceType: "Patient", destinationField: "PatientName",
            resourcePipelineRouteId: _workflowId, destinationConfigurationId: destination.Id));
        await SaveAsync();
        return (destination, policy);
    }

    /// <summary>Source → Transformation → De-identification → Mapping → SQL Server, every step on the destination.</summary>
    private static WorkflowDefinition Definition(Guid id, Guid destinationId, bool transformation = true, bool deIdentification = true)
    {
        var definition = new WorkflowDefinition(id, $"Workflow {id}", 1);
        var onDestination = $$"""{"destinationId":"{{destinationId}}"}""";
        definition.AddNode(WorkflowNodeTypes.EClinicalWorksSource, WorkflowNodeCategory.Source, 10);
        if (transformation)
        {
            definition.AddNode(WorkflowNodeTypes.FhirResourceTransform, WorkflowNodeCategory.Transform, 30, configurationJson: onDestination);
        }

        if (deIdentification)
        {
            definition.AddNode(WorkflowNodeTypes.DeIdentification, WorkflowNodeCategory.Compliance, 40, configurationJson: onDestination);
        }

        definition.AddNode(WorkflowNodeTypes.Mapping, WorkflowNodeCategory.Transform, 50, configurationJson: onDestination);
        definition.AddNode(WorkflowNodeTypes.SqlServerDestination, WorkflowNodeCategory.Destination, 60, configurationJson: onDestination);
        return definition;
    }

    /// <summary>Stores <paramref name="saved"/> (as the save does before cleanup) and runs the cleanup pass.</summary>
    private async Task<WorkflowConfigurationCleanupResult> SaveWorkflowAsync(WorkflowDefinition previous, WorkflowDefinition saved)
    {
        _db.WorkflowDefinitions.Add(saved);
        await SaveAsync();
        return await Cleanup().SoftDeleteUnreferencedAsync(
            _workflowId, previous,
            saved.Nodes.Select(node => new SavedWorkflowNode(node.NodeType, node.ConfigurationJson)).ToArray(),
            CancellationToken.None);
    }

    private Task<int> PolicyRuleCountAsync(Guid policyId) =>
        _db.TransformationRules.CountAsync(rule => rule.DeIdentificationProfileId == policyId);

    private Task<int> WorkflowTransformationRuleCountAsync() =>
        _db.TransformationRules.CountAsync(rule => rule.ResourcePipelineRouteId == _workflowId);

    // ── De-identification ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Saving_without_the_De_identification_step_deletes_its_rules_and_the_workflows_own_destination_does_not_block_it()
    {
        var (destination, policy) = await SeedAsync();

        var result = await SaveWorkflowAsync(
            Definition(_workflowId, destination.Id), Definition(_workflowId, destination.Id, deIdentification: false));

        result.DeIdentificationRulesDeleted.Should().Be(1);
        result.DeIdentificationRulesKeptBecauseUsedBy.Should().BeNull();
        (await PolicyRuleCountAsync(policy.Id)).Should().Be(0);
        // The workflow's own connection no longer names the emptied policy.
        (await _db.DestinationConfigurations.SingleAsync(d => d.Id == destination.Id)).DeIdentificationProfileId.Should().BeNull();
        // The Transformation step is still there: its rules are untouched.
        (await WorkflowTransformationRuleCountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Deleting_the_destination_together_with_its_De_identification_step_deletes_the_rules()
    {
        // The saved definition no longer references the destination at all, but its connection still names the
        // policy. Ownership is judged against what this workflow used BEFORE the save too, so the connection is
        // recognised as the workflow's own rather than an outside user that keeps the rules alive.
        var (destination, policy) = await SeedAsync();
        var replacement = new DestinationConfiguration(
            "New warehouse", DestinationType.SqlServer, new SecretReference("kv", "secret"), "FHIRBridge");
        _db.DestinationConfigurations.Add(replacement);
        await SaveAsync();

        var result = await SaveWorkflowAsync(
            Definition(_workflowId, destination.Id), Definition(_workflowId, replacement.Id, deIdentification: false));

        result.DeIdentificationRulesKeptBecauseUsedBy.Should().BeNull();
        result.DeIdentificationRulesDeleted.Should().Be(1);
        (await PolicyRuleCountAsync(policy.Id)).Should().Be(0);
        (await _db.DestinationConfigurations.SingleAsync(d => d.Id == destination.Id)).DeIdentificationProfileId.Should().BeNull();
    }

    [Fact]
    public async Task A_removed_destination_still_used_by_another_workflow_keeps_the_rules()
    {
        var (destination, policy) = await SeedAsync();
        _db.WorkflowDefinitions.Add(Definition(Guid.NewGuid(), destination.Id));
        var replacement = new DestinationConfiguration(
            "New warehouse", DestinationType.SqlServer, new SecretReference("kv", "secret"), "FHIRBridge");
        _db.DestinationConfigurations.Add(replacement);
        await SaveAsync();

        var result = await SaveWorkflowAsync(
            Definition(_workflowId, destination.Id), Definition(_workflowId, replacement.Id, deIdentification: false));

        result.DeIdentificationRulesKeptBecauseUsedBy.Should().ContainSingle().Which.Should().Contain("Warehouse");
        (await PolicyRuleCountAsync(policy.Id)).Should().Be(1);
    }

    [Fact]
    public async Task A_save_that_keeps_the_De_identification_step_deletes_nothing()
    {
        var (destination, policy) = await SeedAsync();

        var result = await SaveWorkflowAsync(Definition(_workflowId, destination.Id), Definition(_workflowId, destination.Id));

        result.DeIdentificationRulesDeleted.Should().Be(0);
        (await PolicyRuleCountAsync(policy.Id)).Should().Be(1);
    }

    [Fact]
    public async Task Rules_are_kept_when_another_workflow_uses_the_same_destination_connection()
    {
        var (destination, policy) = await SeedAsync();
        _db.WorkflowDefinitions.Add(Definition(Guid.NewGuid(), destination.Id));
        await SaveAsync();

        var result = await SaveWorkflowAsync(
            Definition(_workflowId, destination.Id), Definition(_workflowId, destination.Id, deIdentification: false));

        result.DeIdentificationRulesDeleted.Should().Be(0);
        result.DeIdentificationRulesKeptBecauseUsedBy.Should().ContainSingle().Which.Should().Contain("Warehouse");
        (await PolicyRuleCountAsync(policy.Id)).Should().Be(1);
        (await _db.DestinationConfigurations.SingleAsync(d => d.Id == destination.Id)).DeIdentificationProfileId.Should().Be(policy.Id);
    }

    [Fact]
    public async Task Rules_are_kept_when_another_workflow_names_the_policy_itself()
    {
        var (destination, policy) = await SeedAsync();
        var other = new WorkflowDefinition(Guid.NewGuid(), "Other pipeline", 1);
        other.AddNode(WorkflowNodeTypes.DeIdentification, WorkflowNodeCategory.Compliance, 40,
            configurationJson: $$"""{"profileId":"{{policy.Id}}"}""");
        _db.WorkflowDefinitions.Add(other);
        await SaveAsync();

        var result = await SaveWorkflowAsync(
            Definition(_workflowId, destination.Id), Definition(_workflowId, destination.Id, deIdentification: false));

        result.DeIdentificationRulesKeptBecauseUsedBy.Should().ContainSingle().Which.Should().Contain("Other pipeline");
        (await PolicyRuleCountAsync(policy.Id)).Should().Be(1);
    }

    [Fact]
    public async Task Rules_are_kept_when_a_V1_route_writes_to_the_connection()
    {
        var (destination, policy) = await SeedAsync();
        var mappingProfile = new MappingProfile("Patient → dbo.Patient", "Patient", Guid.NewGuid(), destination.Id, "dbo.Patient", []);
        _db.MappingProfiles.Add(mappingProfile);
        await SaveAsync();
        _db.ResourcePipelineRoutes.Add(new ResourcePipelineRoute(
            null, mappingProfile.Id, IngestionMode.ScheduledPull, null, null, isEnabled: true, priority: 1));
        await SaveAsync();

        var result = await SaveWorkflowAsync(
            Definition(_workflowId, destination.Id), Definition(_workflowId, destination.Id, deIdentification: false));

        result.DeIdentificationRulesKeptBecauseUsedBy.Should().ContainSingle().Which.Should().Contain("Warehouse");
        (await PolicyRuleCountAsync(policy.Id)).Should().Be(1);
    }

    [Fact]
    public async Task Rules_are_kept_when_a_connection_this_workflow_does_not_use_names_the_policy()
    {
        var (destination, policy) = await SeedAsync();
        var unrelated = new DestinationConfiguration(
            "Unrelated", DestinationType.SqlServer, new SecretReference("kv", "secret"), "Other");
        unrelated.SetDeIdentificationProfile(policy.Id);
        _db.DestinationConfigurations.Add(unrelated);
        await SaveAsync();

        var result = await SaveWorkflowAsync(
            Definition(_workflowId, destination.Id), Definition(_workflowId, destination.Id, deIdentification: false));

        result.DeIdentificationRulesKeptBecauseUsedBy.Should().ContainSingle().Which.Should().Contain("Unrelated");
        (await PolicyRuleCountAsync(policy.Id)).Should().Be(1);
    }

    [Fact]
    public async Task Another_workflows_policy_is_never_touched()
    {
        var (destination, _) = await SeedAsync();
        var othersPolicy = new DeIdentificationProfile(Guid.NewGuid().ToString());
        _db.DeIdentificationProfiles.Add(othersPolicy);
        _db.TransformationRules.Add(new TransformationRule(
            TransformScope.ResourceType, TransformNodeType.HashingMasking, """{"mode":"hash"}""",
            resourceType: "Patient", sourceField: "$.birthDate", executionPhase: TransformExecutionPhase.PreMapping,
            deIdentificationProfileId: othersPolicy.Id));
        await SaveAsync();

        await SaveWorkflowAsync(
            Definition(_workflowId, destination.Id), Definition(_workflowId, destination.Id, deIdentification: false));

        (await PolicyRuleCountAsync(othersPolicy.Id)).Should().Be(1);
    }

    [Fact]
    public async Task Running_the_cleanup_again_is_harmless()
    {
        var (destination, policy) = await SeedAsync();
        var previous = Definition(_workflowId, destination.Id);
        var saved = Definition(_workflowId, destination.Id, deIdentification: false);
        await SaveWorkflowAsync(previous, saved);

        var again = await Cleanup().SoftDeleteUnreferencedAsync(
            _workflowId, previous, saved.Nodes.Select(n => new SavedWorkflowNode(n.NodeType, n.ConfigurationJson)).ToArray(),
            CancellationToken.None);

        again.DeIdentificationRulesDeleted.Should().Be(0);
        (await PolicyRuleCountAsync(policy.Id)).Should().Be(0);
    }

    // ── Transformation ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Saving_without_the_Transformation_step_deletes_that_destinations_rules_only()
    {
        var (destination, policy) = await SeedAsync();

        var result = await SaveWorkflowAsync(
            Definition(_workflowId, destination.Id), Definition(_workflowId, destination.Id, transformation: false));

        result.TransformationStepRulesDeleted.Should().Be(1);
        (await WorkflowTransformationRuleCountAsync()).Should().Be(0);
        (await PolicyRuleCountAsync(policy.Id)).Should().Be(1);   // De-identification still on the canvas
    }

    [Fact]
    public async Task A_save_that_keeps_the_Transformation_step_deletes_nothing()
    {
        var (destination, _) = await SeedAsync();

        var result = await SaveWorkflowAsync(Definition(_workflowId, destination.Id), Definition(_workflowId, destination.Id));

        result.TransformationStepRulesDeleted.Should().Be(0);
        (await WorkflowTransformationRuleCountAsync()).Should().Be(1);
    }
}
