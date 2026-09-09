using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Persistence;
using FHIRBridge.Runtime.Application.Workflows.Storage;
using FHIRBridge.Runtime.Domain.Workflows;
using FluentAssertions;
using Xunit;

namespace FHIRBridge.UnitTests.Configuration;

/// <summary>
/// The retirement pass a workflow save runs for configuration it just stopped referencing. Everything
/// retired here is a soft delete in production (MappingProfile/TransformationRule are ISoftDeletable, so
/// AuditingSaveChangesInterceptor turns the repository's Remove into an IsDeleted stamp and the global
/// query filter hides it) — these tests assert WHAT gets retired, which is the part the service decides.
/// </summary>
public sealed class WorkflowConfigurationCleanupServiceTests
{
    private static readonly Guid WorkflowId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task Retires_the_mapping_profiles_and_rules_of_a_removed_destination()
    {
        var fixture = await FixtureAsync();

        // The re-save keeps the source node but drops the SQL destination entirely.
        var result = await fixture.Service.SoftDeleteUnreferencedAsync(
            WorkflowId, fixture.Previous, [SourceNodeJson], CancellationToken.None);

        result.MappingProfileIds.Should().BeEquivalentTo([fixture.MappingProfileId]);
        result.TransformationRuleIds.Should().BeEquivalentTo([fixture.WorkflowRuleId]);

        (await fixture.Repository.GetMappingProfileAsync(fixture.MappingProfileId, CancellationToken.None))
            .Should().BeNull("the profile was retired");
    }

    [Fact]
    public async Task Leaves_the_tenant_wide_rule_tiers_alone()
    {
        var fixture = await FixtureAsync();

        await fixture.Service.SoftDeleteUnreferencedAsync(
            WorkflowId, fixture.Previous, [SourceNodeJson], CancellationToken.None);

        // Field/ResourceType/DestinationType/Global rules are shared with every other pipeline — a canvas
        // edit must never retire them, however completely this workflow walks away from the destination.
        fixture.Rules.Deleted.Should().NotContain(fixture.FieldRuleId);
    }

    [Fact]
    public async Task Keeps_a_mapping_profile_another_workflow_still_references()
    {
        var fixture = await FixtureAsync();

        // A second workflow points at the same profile (e.g. one promoted to master and reused).
        var other = new WorkflowDefinition(Guid.NewGuid(), "Other", version: 1);
        other.AddNode(
            "MappingNode", WorkflowNodeCategory.Transform, rank: 60,
            configurationJson: $"{{\"mappingProfileId\":\"{fixture.MappingProfileId}\"}}");
        await fixture.Store.SaveAsync(other, CancellationToken.None);

        var result = await fixture.Service.SoftDeleteUnreferencedAsync(
            WorkflowId, fixture.Previous, [SourceNodeJson], CancellationToken.None);

        result.MappingProfileIds.Should().BeEmpty();
        (await fixture.Repository.GetMappingProfileAsync(fixture.MappingProfileId, CancellationToken.None))
            .Should().NotBeNull();
    }

    [Fact]
    public async Task Keeps_the_rules_when_the_replacement_destination_is_the_same_type()
    {
        var fixture = await FixtureAsync();

        // Swapping one SQL Server destination for another leaves the rules applicable — the Workflow tier
        // keys on destination TYPE, so they are still this workflow's live rules for that type.
        var replacement = new DestinationConfiguration(
            "Replacement SQL", DestinationType.SqlServer, new SecretReference("kv", "secret2"), "FHIRBridge");
        await fixture.Repository.AddDestinationAsync(replacement, CancellationToken.None);

        var result = await fixture.Service.SoftDeleteUnreferencedAsync(
            WorkflowId,
            fixture.Previous,
            [SourceNodeJson, $"{{\"destinationId\":\"{replacement.Id}\"}}"],
            CancellationToken.None);

        result.TransformationRuleIds.Should().BeEmpty();
        // The dropped destination's own profile is still retired — it targets a destination this workflow
        // no longer writes to, whatever type replaced it.
        result.MappingProfileIds.Should().BeEquivalentTo([fixture.MappingProfileId]);
    }

    [Fact]
    public async Task Retires_nothing_when_the_destination_is_still_there()
    {
        var fixture = await FixtureAsync();

        var result = await fixture.Service.SoftDeleteUnreferencedAsync(
            WorkflowId, fixture.Previous, [SourceNodeJson, fixture.DestinationNodeJson, fixture.MappingNodeJson],
            CancellationToken.None);

        result.RetiredAnything.Should().BeFalse();
    }

    [Fact]
    public async Task Retires_nothing_for_a_brand_new_workflow()
    {
        var fixture = await FixtureAsync();

        var result = await fixture.Service.SoftDeleteUnreferencedAsync(
            WorkflowId, previousDefinition: null, [SourceNodeJson], CancellationToken.None);

        result.RetiredAnything.Should().BeFalse();
    }

    private const string SourceNodeJson = "{\"sourceConnectionId\":\"22222222-2222-2222-2222-222222222222\"}";

    private sealed record Fixture(
        WorkflowConfigurationCleanupService Service,
        InMemoryConfigurationRepository Repository,
        FakeTransformationRuleRepository Rules,
        IWorkflowDefinitionStore Store,
        WorkflowDefinition Previous,
        Guid MappingProfileId,
        Guid WorkflowRuleId,
        Guid FieldRuleId,
        string DestinationNodeJson,
        string MappingNodeJson);

    /// <summary>A saved workflow of Source → Mapping → SQL Server, with one workflow-scoped rule for that
    /// destination type and one tenant-wide Field-scoped rule that must survive.</summary>
    private static async Task<Fixture> FixtureAsync()
    {
        var repository = new InMemoryConfigurationRepository();

        var destination = new DestinationConfiguration(
            "Warehouse", DestinationType.SqlServer, new SecretReference("kv", "secret"), "FHIRBridge");
        await repository.AddDestinationAsync(destination, CancellationToken.None);

        var profile = new MappingProfile(
            "Patient → dbo.Patient", "Patient", Guid.Parse("22222222-2222-2222-2222-222222222222"),
            destination.Id, "dbo.Patient", []);
        await repository.AddMappingProfileAsync(profile, CancellationToken.None);

        var workflowRule = new TransformationRule(
            TransformScope.Workflow, TransformNodeType.StringNormalization, "{}", DestinationType.SqlServer,
            resourceType: "Patient", destinationField: "Name", resourcePipelineRouteId: WorkflowId);
        var fieldRule = new TransformationRule(
            TransformScope.Field, TransformNodeType.StringNormalization, "{}", DestinationType.SqlServer,
            resourceType: "Patient", destinationField: "Name");
        var rules = new FakeTransformationRuleRepository([workflowRule, fieldRule]);

        var destinationNodeJson = $"{{\"destinationId\":\"{destination.Id}\"}}";
        var mappingNodeJson = $"{{\"destinationId\":\"{destination.Id}\",\"mappingProfileIds\":{{\"Patient\":\"{profile.Id}\"}}}}";

        var previous = new WorkflowDefinition(WorkflowId, "Warehouse feed", version: 1);
        previous.AddNode("EpicSourceNode", WorkflowNodeCategory.Source, rank: 10, configurationJson: SourceNodeJson);
        previous.AddNode("MappingNode", WorkflowNodeCategory.Transform, rank: 60, configurationJson: mappingNodeJson);
        previous.AddNode("SqlServerDestinationNode", WorkflowNodeCategory.Destination, rank: 100, configurationJson: destinationNodeJson);

        var store = new InMemoryWorkflowDefinitionStore();
        await store.SaveAsync(previous, CancellationToken.None);

        return new Fixture(
            new WorkflowConfigurationCleanupService(repository, rules, store),
            repository, rules, store, previous,
            profile.Id, workflowRule.Id, fieldRule.Id, destinationNodeJson, mappingNodeJson);
    }

    /// <summary>Only the two members the cleanup pass uses are real; the rest of the resolver surface is
    /// not exercised here.</summary>
    private sealed class FakeTransformationRuleRepository : ITransformationRuleRepository
    {
        private readonly List<TransformationRule> _rules;

        public FakeTransformationRuleRepository(IEnumerable<TransformationRule> rules) => _rules = [.. rules];

        public List<Guid> Deleted { get; } = [];

        public Task<IReadOnlyList<TransformationRule>> ListAsync(
            TransformScope? scope, DestinationType? destinationType, string? resourceType, string? destinationField,
            Guid? resourcePipelineRouteId, string? sourceSystem, string? sourceField,
            TransformExecutionPhase? executionPhase, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TransformationRule>>(_rules
                .Where(x => executionPhase == null
                    ? x.ExecutionPhase != TransformExecutionPhase.FhirResource
                    : x.ExecutionPhase == executionPhase)
                .Where(x => scope == null || x.Scope == scope)
                .Where(x => destinationType == null || x.DestinationType == destinationType)
                .Where(x => resourcePipelineRouteId == null || x.ResourcePipelineRouteId == resourcePipelineRouteId)
                .ToList());

        public Task DeleteAsync(TransformationRule rule, CancellationToken cancellationToken)
        {
            _rules.Remove(rule);
            Deleted.Add(rule.Id);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TransformationRule>> GetWorkflowScopedAsync(
            Guid resourcePipelineRouteId, string resourceType, string destinationField, string? sourceSystem,
            string? sourceField, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<TransformationRule>> GetFieldScopedAsync(
            string resourceType, string destinationField, string? sourceSystem, string? sourceField,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<TransformationRule>> GetResourceTypeScopedAsync(
            string resourceType, string? destinationField, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TransformationRule>> GetDestinationTypeScopedAsync(
            DestinationType destinationType, string? destinationField, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TransformationRule>> GetGlobalScopedAsync(
            string? destinationField, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<TransformationRule>> GetPreMappingRulesAsync(
            Guid deIdentificationProfileId, string resourceType, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TransformationRule>> GetFhirResourceRulesAsync(
            TransformScope scope, string resourceType, string? sourceField, DestinationType? destinationType,
            Guid? resourcePipelineRouteId, string? sourceSystem, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TransformationRule>> GetPendingWorkflowRulesAsync(
            IReadOnlyCollection<DestinationType> destinationTypes, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TransformationRule?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AddAsync(TransformationRule rule, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task UpdateAsync(TransformationRule rule, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
