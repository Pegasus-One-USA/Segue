using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Governance;
using FHIRBridge.Infrastructure.Persistence;
using FHIRBridge.Runtime.Application.Workflows.Catalog;
using FHIRBridge.Runtime.Domain.Workflows;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace FHIRBridge.UnitTests.Governance;

/// <summary>
/// Removing a workflow's last De-identification step deletes the rules of the policy that workflow owns — the
/// one named with its id. They used to stay: the builder re-adopted the policy by name on the next destination
/// save, put the step back, and every run kept redacting with them.
///
/// Runs the real service over the real EF repositories (in-memory provider), and the real Safe Harbor service
/// for what a run applies afterwards.
/// </summary>
public sealed class WorkflowDeIdentificationRuleDeletionTests : IDisposable
{
    private const string PatientJson = """{"resourceType":"Patient","id":"p1","name":[{"given":["DB","Tester"]}]}""";

    private readonly FHIRBridgeDbContext _db = new(
        new DbContextOptionsBuilder<FHIRBridgeDbContext>()
            // A unique name isolates each test; no InMemoryDatabaseRoot of its own, since every new root builds another
            // EF service provider and past twenty EF fails whichever test happens to create the next one.
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    public void Dispose() => _db.Dispose();

    /// <summary>SaveChanges, stamping the audit column the app's own interceptor would (this context has none).</summary>
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

    private DeIdentificationProfileService Service() =>
        new(new EfDeIdentificationProfileRepository(_db), new Mock<IDeIdentificationService>().Object,
            new EfTransformationRuleRepository(_db));

    private async Task<DeIdentificationProfile> PolicyAsync(string name, params string[] maskedPaths)
    {
        var profile = new DeIdentificationProfile(name);
        _db.DeIdentificationProfiles.Add(profile);
        foreach (var path in maskedPaths)
        {
            _db.TransformationRules.Add(new TransformationRule(
                TransformScope.ResourceType, TransformNodeType.HashingMasking, """{"mode":"mask","keepLength":"4"}""",
                resourceType: "Patient", sourceField: path, executionPhase: TransformExecutionPhase.PreMapping,
                deIdentificationProfileId: profile.Id));
        }

        await SaveAsync();
        return profile;
    }

    private Task<int> LiveRuleCountAsync(Guid profileId) =>
        _db.TransformationRules.CountAsync(rule => rule.DeIdentificationProfileId == profileId);

    private async Task AddWorkflowReferencingAsync(Guid workflowId, string name, Guid profileId)
    {
        var workflow = new WorkflowDefinition(workflowId, name, 1);
        workflow.AddNode(WorkflowNodeTypes.DeIdentification, WorkflowNodeCategory.Compliance, 30,
            configurationJson: $$"""{"profileId":"{{profileId}}"}""");
        _db.WorkflowDefinitions.Add(workflow);
        await SaveAsync();
    }

    [Fact]
    public async Task Deletes_every_rule_of_the_workflows_own_policy_and_keeps_the_policy_empty()
    {
        var workflowId = Guid.NewGuid();
        var owned = await PolicyAsync(workflowId.ToString(), "$.name[*].given[*]", "$.address[*].line[*]");

        var result = await Service().DeleteWorkflowPolicyRulesAsync(workflowId, CancellationToken.None);

        result.Deleted.Should().Be(2);
        result.UsedElsewhere.Should().BeEmpty();
        (await LiveRuleCountAsync(owned.Id)).Should().Be(0);
        // Kept, empty: a De-identification step added again later starts with no rules.
        (await _db.DeIdentificationProfiles.CountAsync(profile => profile.Id == owned.Id)).Should().Be(1);
    }

    [Fact]
    public async Task Leaves_other_workflows_policies_and_transformation_rules_alone()
    {
        var workflowId = Guid.NewGuid();
        await PolicyAsync(workflowId.ToString(), "$.name[*].given[*]");
        var otherWorkflows = await PolicyAsync(Guid.NewGuid().ToString(), "$.name[*].given[*]");
        var shared = await PolicyAsync("Hospital-wide Safe Harbor", "$.birthDate");
        _db.TransformationRules.Add(new TransformationRule(
            TransformScope.Workflow, TransformNodeType.ConcatenationTemplating, """{"template":"{0} {1}"}""",
            destinationType: DestinationType.SqlServer, resourceType: "Patient", destinationField: "PatientName",
            resourcePipelineRouteId: workflowId));
        await SaveAsync();

        await Service().DeleteWorkflowPolicyRulesAsync(workflowId, CancellationToken.None);

        (await LiveRuleCountAsync(otherWorkflows.Id)).Should().Be(1);
        (await LiveRuleCountAsync(shared.Id)).Should().Be(1);
        (await _db.TransformationRules.CountAsync(rule => rule.ResourcePipelineRouteId == workflowId)).Should().Be(1);
    }

    [Fact]
    public async Task Refuses_and_deletes_nothing_when_a_destination_connection_uses_the_policy()
    {
        var workflowId = Guid.NewGuid();
        var owned = await PolicyAsync(workflowId.ToString(), "$.name[*].given[*]");
        var destination = new DestinationConfiguration(
            "Warehouse", DestinationType.SqlServer, new SecretReference("kv", "secret"), "FHIRBridge");
        destination.SetDeIdentificationProfile(owned.Id);
        _db.DestinationConfigurations.Add(destination);
        await SaveAsync();

        var result = await Service().DeleteWorkflowPolicyRulesAsync(workflowId, CancellationToken.None);

        result.Deleted.Should().Be(0);
        result.UsedElsewhere.Should().ContainSingle().Which.Should().Contain("Warehouse");
        (await LiveRuleCountAsync(owned.Id)).Should().Be(1);
    }

    [Fact]
    public async Task Refuses_and_deletes_nothing_when_another_workflow_uses_the_policy()
    {
        var workflowId = Guid.NewGuid();
        var owned = await PolicyAsync(workflowId.ToString(), "$.name[*].given[*]");
        await AddWorkflowReferencingAsync(Guid.NewGuid(), "Other pipeline", owned.Id);

        var result = await Service().DeleteWorkflowPolicyRulesAsync(workflowId, CancellationToken.None);

        result.Deleted.Should().Be(0);
        result.UsedElsewhere.Should().ContainSingle().Which.Should().Contain("Other pipeline");
        (await LiveRuleCountAsync(owned.Id)).Should().Be(1);
    }

    [Fact]
    public async Task The_workflows_own_saved_definition_referencing_its_policy_does_not_block_the_delete()
    {
        var workflowId = Guid.NewGuid();
        var owned = await PolicyAsync(workflowId.ToString(), "$.name[*].given[*]");
        await AddWorkflowReferencingAsync(workflowId, "This pipeline", owned.Id);

        var result = await Service().DeleteWorkflowPolicyRulesAsync(workflowId, CancellationToken.None);

        result.Deleted.Should().Be(1);
    }

    [Fact]
    public async Task A_workflow_without_a_policy_of_its_own_deletes_nothing_and_succeeds()
    {
        var shared = await PolicyAsync("Hospital-wide Safe Harbor", "$.birthDate");

        var result = await Service().DeleteWorkflowPolicyRulesAsync(Guid.NewGuid(), CancellationToken.None);

        result.Deleted.Should().Be(0);
        result.UsedElsewhere.Should().BeEmpty();
        (await LiveRuleCountAsync(shared.Id)).Should().Be(1);
    }

    [Fact]
    public async Task A_run_after_the_delete_applies_none_of_the_deleted_rules()
    {
        var workflowId = Guid.NewGuid();
        var owned = await PolicyAsync(workflowId.ToString(), "$.name[*].given[*]");
        var safeHarbor = new SafeHarborDeIdentificationService(new EfTransformationRuleRepository(_db));
        var request = new DeIdentificationRequest("Patient", "p1", PatientJson, [], owned.Id);
        (await safeHarbor.DeIdentifyAsync(request, CancellationToken.None)).Json.Should().NotContain("Tester");

        await Service().DeleteWorkflowPolicyRulesAsync(workflowId, CancellationToken.None);

        var after = await safeHarbor.DeIdentifyAsync(request, CancellationToken.None);
        after.Hops.Should().BeEmpty();
        after.Json.Should().Contain("Tester");
    }

    [Fact]
    public async Task Rules_added_after_the_delete_are_the_only_ones_the_policy_has()
    {
        var workflowId = Guid.NewGuid();
        var owned = await PolicyAsync(workflowId.ToString(), "$.name[*].given[*]", "$.address[*].line[*]");
        await Service().DeleteWorkflowPolicyRulesAsync(workflowId, CancellationToken.None);

        _db.TransformationRules.Add(new TransformationRule(
            TransformScope.ResourceType, TransformNodeType.HashingMasking, """{"mode":"redact"}""",
            resourceType: "Patient", sourceField: "$.birthDate", executionPhase: TransformExecutionPhase.PreMapping,
            deIdentificationProfileId: owned.Id));
        await SaveAsync();

        var rules = await new EfTransformationRuleRepository(_db).GetPreMappingRulesAsync(owned.Id, "Patient", CancellationToken.None);
        rules.Should().ContainSingle().Which.SourceField.Should().Be("$.birthDate");
    }
}
