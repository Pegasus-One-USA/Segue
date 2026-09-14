using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Services.Transforms;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FluentAssertions;
using Moq;

namespace FHIRBridge.UnitTests.Transforms;

/// <summary>Covers the resolver's PostMapping resolution after WORKFLOW_V3_PLAN.md Step 3 removed every tier
/// except Workflow: a rule belongs to exactly one workflow (its own attached rules, or — only when opted in —
/// its still-unattached pending ones), and there is no broader fallback any more. Also covers the
/// "specific beats blanket" source-system/source-field preference within that one tier.</summary>
public sealed class EffectiveRuleResolverTests
{
    private const string ResourceType = "Patient";
    private const string DestinationField = "BirthDate";
    private static readonly Guid WorkflowId = Guid.NewGuid();

    private static Mock<ITransformationRuleRepository> CreateRepositoryMock()
    {
        var repository = new Mock<ITransformationRuleRepository>();
        repository
            .Setup(x => x.GetWorkflowScopedAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[]);
        repository
            .Setup(x => x.GetPendingWorkflowScopedAsync(It.IsAny<DestinationType>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[]);
        return repository;
    }

    /// <summary>A Workflow-scope rule authored before its workflow existed carries no ResourcePipelineRouteId,
    /// so no tier query matches it — which is correct at run time (it belongs to no pipeline) and wrong at save
    /// time, where the mapping profile is written BEFORE the workflow that would attach it. Opting in is the
    /// only way to see it; not opting in must keep it invisible.</summary>
    private static Mock<ITransformationRuleRepository> WithPendingRule(out TransformationRule pendingRule)
    {
        var repository = CreateRepositoryMock();
        pendingRule = new TransformationRule(
            TransformScope.Workflow, TransformNodeType.DateMathAge, "{}", resourceType: ResourceType,
            destinationField: DestinationField, destinationType: DestinationType.SqlServer,
            expectedValueType: MappingValueType.Integer);
        var rule = pendingRule;
        repository
            .Setup(x => x.GetPendingWorkflowScopedAsync(
                DestinationType.SqlServer, ResourceType, DestinationField, It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[rule]);
        return repository;
    }

    [Fact]
    public async Task Pending_workflow_rule_is_invisible_unless_the_caller_opts_in()
    {
        var repository = WithPendingRule(out _);

        var resolver = new EffectiveRuleResolver(repository.Object);
        var result = await resolver.ResolveAsync(
            DestinationType.SqlServer, ResourceType, DestinationField, null, null, null, CancellationToken.None);

        result.Should().BeEmpty();
        repository.Verify(
            x => x.GetPendingWorkflowScopedAsync(
                It.IsAny<DestinationType>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Pending_workflow_rule_resolves_for_an_unsaved_workflow_when_opted_in()
    {
        var repository = WithPendingRule(out var pendingRule);

        var resolver = new EffectiveRuleResolver(repository.Object);
        var result = await resolver.ResolveAsync(
            DestinationType.SqlServer, ResourceType, DestinationField, null, null, null, CancellationToken.None,
            includePendingWorkflowRules: true);

        result.Should().ContainSingle().Which.Should().Be(pendingRule);
    }

    [Fact]
    public async Task Attached_workflow_rule_beats_a_pending_one()
    {
        var repository = WithPendingRule(out _);
        var attachedRule = new TransformationRule(
            TransformScope.Workflow, TransformNodeType.NumberCast, "{}", resourceType: ResourceType,
            destinationField: DestinationField, resourcePipelineRouteId: WorkflowId);
        repository
            .Setup(x => x.GetWorkflowScopedAsync(
                WorkflowId, ResourceType, DestinationField, It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[attachedRule]);

        var resolver = new EffectiveRuleResolver(repository.Object);
        var result = await resolver.ResolveAsync(
            DestinationType.SqlServer, ResourceType, DestinationField, WorkflowId, null, null, CancellationToken.None,
            includePendingWorkflowRules: true);

        result.Should().ContainSingle().Which.Should().Be(attachedRule);
    }

    [Fact]
    public async Task Workflow_rule_resolves_when_a_workflow_id_is_supplied()
    {
        var repository = CreateRepositoryMock();
        var workflowRule = new TransformationRule(
            TransformScope.Workflow, TransformNodeType.DefaultNullHandling, "{}",
            resourceType: ResourceType, destinationField: DestinationField, resourcePipelineRouteId: WorkflowId);
        repository
            .Setup(x => x.GetWorkflowScopedAsync(WorkflowId, ResourceType, DestinationField, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[workflowRule]);

        var resolver = new EffectiveRuleResolver(repository.Object);
        var result = await resolver.ResolveAsync(DestinationType.SqlServer, ResourceType, DestinationField, WorkflowId, null, null, CancellationToken.None);

        result.Should().ContainSingle().Which.Should().Be(workflowRule);
    }

    [Fact]
    public async Task Returns_empty_when_no_workflow_id_is_supplied_and_nothing_pending_matches()
    {
        var repository = CreateRepositoryMock();
        var resolver = new EffectiveRuleResolver(repository.Object);

        var result = await resolver.ResolveAsync(DestinationType.FhirRepository, ResourceType, DestinationField, null, null, null, CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task Returns_empty_when_the_workflow_has_no_rule_for_this_field()
    {
        var repository = CreateRepositoryMock();
        var resolver = new EffectiveRuleResolver(repository.Object);

        var result = await resolver.ResolveAsync(DestinationType.SqlServer, ResourceType, DestinationField, WorkflowId, null, null, CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task A_source_specific_workflow_rule_beats_a_blanket_any_source_rule()
    {
        var repository = CreateRepositoryMock();
        var anySource = new TransformationRule(
            TransformScope.Workflow, TransformNodeType.ValueCodeMapping, "{}", resourceType: ResourceType,
            destinationField: DestinationField, resourcePipelineRouteId: WorkflowId);
        var epicOnly = new TransformationRule(
            TransformScope.Workflow, TransformNodeType.StatusEnumCoercion, "{}",
            resourceType: ResourceType, destinationField: DestinationField, resourcePipelineRouteId: WorkflowId,
            sourceSystem: "Epic");
        repository
            .Setup(x => x.GetWorkflowScopedAsync(WorkflowId, ResourceType, DestinationField, "Epic", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[anySource, epicOnly]);

        var resolver = new EffectiveRuleResolver(repository.Object);
        var result = await resolver.ResolveAsync(
            DestinationType.SqlServer, ResourceType, DestinationField, WorkflowId, "Epic", null, CancellationToken.None);

        result.Should().ContainSingle().Which.Should().Be(epicOnly);
    }

    [Fact]
    public async Task A_blanket_workflow_rule_still_applies_when_no_source_specific_row_matches()
    {
        var repository = CreateRepositoryMock();
        var anySource = new TransformationRule(
            TransformScope.Workflow, TransformNodeType.ValueCodeMapping, "{}", resourceType: ResourceType,
            destinationField: DestinationField, resourcePipelineRouteId: WorkflowId);
        repository
            .Setup(x => x.GetWorkflowScopedAsync(WorkflowId, ResourceType, DestinationField, "Healow", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[anySource]);

        var resolver = new EffectiveRuleResolver(repository.Object);
        var result = await resolver.ResolveAsync(
            DestinationType.SqlServer, ResourceType, DestinationField, WorkflowId, "Healow", null, CancellationToken.None);

        result.Should().ContainSingle().Which.Should().Be(anySource);
    }

    [Fact]
    public async Task A_source_field_specific_workflow_rule_beats_a_blanket_any_field_rule()
    {
        var repository = CreateRepositoryMock();
        var anyField = new TransformationRule(
            TransformScope.Workflow, TransformNodeType.ValueCodeMapping, "{}", resourceType: ResourceType,
            destinationField: DestinationField, resourcePipelineRouteId: WorkflowId);
        var identifierValueOnly = new TransformationRule(
            TransformScope.Workflow, TransformNodeType.IdentifierFormatting, "{}",
            resourceType: ResourceType, destinationField: DestinationField, resourcePipelineRouteId: WorkflowId,
            sourceField: "identifier.value");
        repository
            .Setup(x => x.GetWorkflowScopedAsync(WorkflowId, ResourceType, DestinationField, null, "identifier.value", It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[anyField, identifierValueOnly]);

        var resolver = new EffectiveRuleResolver(repository.Object);
        var result = await resolver.ResolveAsync(
            DestinationType.SqlServer, ResourceType, DestinationField, WorkflowId, null, "identifier.value", CancellationToken.None);

        result.Should().ContainSingle().Which.Should().Be(identifierValueOnly);
    }
}
