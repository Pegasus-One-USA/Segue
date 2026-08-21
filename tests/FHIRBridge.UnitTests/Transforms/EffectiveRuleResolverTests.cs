using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Services.Transforms;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FluentAssertions;
using Moq;

namespace FHIRBridge.UnitTests.Transforms;

/// <summary>Covers the 5-level fallback chain: most specific tier with a matching row wins outright, no merging
/// across tiers, and a field-specific row beats a blanket (DestinationField == null) row within the same tier.</summary>
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
            .Setup(x => x.GetFieldScopedAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[]);
        repository
            .Setup(x => x.GetResourceTypeScopedAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[]);
        repository
            .Setup(x => x.GetDestinationTypeScopedAsync(It.IsAny<DestinationType>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[]);
        repository
            .Setup(x => x.GetGlobalScopedAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[]);
        return repository;
    }

    [Fact]
    public async Task Falls_back_to_Global_when_no_narrower_tier_has_a_rule()
    {
        var repository = CreateRepositoryMock();
        var globalRule = new TransformationRule(TransformScope.Global, TransformNodeType.DefaultNullHandling, "{}");
        repository
            .Setup(x => x.GetGlobalScopedAsync(DestinationField, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[globalRule]);

        var resolver = new EffectiveRuleResolver(repository.Object);
        var result = await resolver.ResolveAsync(DestinationType.FhirRepository, ResourceType, DestinationField, null, null, null, CancellationToken.None);

        result.Should().ContainSingle().Which.Should().Be(globalRule);
    }

    [Fact]
    public async Task ResourceType_tier_overrides_DestinationType_and_Global_when_present()
    {
        var repository = CreateRepositoryMock();
        var resourceTypeRule = new TransformationRule(
            TransformScope.ResourceType, TransformNodeType.DateMathAge, "{}", resourceType: ResourceType);
        repository
            .Setup(x => x.GetResourceTypeScopedAsync(ResourceType, DestinationField, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[resourceTypeRule]);

        var resolver = new EffectiveRuleResolver(repository.Object);
        var result = await resolver.ResolveAsync(DestinationType.SqlServer, ResourceType, DestinationField, null, null, null, CancellationToken.None);

        result.Should().ContainSingle().Which.Should().Be(resourceTypeRule);
        repository.Verify(x => x.GetDestinationTypeScopedAsync(It.IsAny<DestinationType>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Field_tier_overrides_ResourceType_tier()
    {
        var repository = CreateRepositoryMock();
        repository
            .Setup(x => x.GetResourceTypeScopedAsync(ResourceType, DestinationField, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[new TransformationRule(TransformScope.ResourceType, TransformNodeType.DateMathAge, "{}")]);

        var fieldRule = new TransformationRule(
            TransformScope.Field, TransformNodeType.DateTimeFormat, "{}", resourceType: ResourceType, destinationField: DestinationField);
        repository
            .Setup(x => x.GetFieldScopedAsync(ResourceType, DestinationField, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[fieldRule]);

        var resolver = new EffectiveRuleResolver(repository.Object);
        var result = await resolver.ResolveAsync(DestinationType.SqlServer, ResourceType, DestinationField, null, null, null, CancellationToken.None);

        result.Should().ContainSingle().Which.Should().Be(fieldRule);
    }

    [Fact]
    public async Task Workflow_tier_beats_every_other_tier_when_a_workflow_id_is_supplied()
    {
        var repository = CreateRepositoryMock();
        repository
            .Setup(x => x.GetFieldScopedAsync(ResourceType, DestinationField, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[new TransformationRule(TransformScope.Field, TransformNodeType.DateTimeFormat, "{}")]);

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
    public async Task Within_a_tier_a_field_specific_row_beats_a_blanket_row()
    {
        var repository = CreateRepositoryMock();
        var blanket = new TransformationRule(TransformScope.Global, TransformNodeType.DefaultNullHandling, "{}");
        var fieldSpecific = new TransformationRule(TransformScope.Global, TransformNodeType.DateTimeFormat, "{}", destinationField: DestinationField);
        repository
            .Setup(x => x.GetGlobalScopedAsync(DestinationField, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[blanket, fieldSpecific]);

        var resolver = new EffectiveRuleResolver(repository.Object);
        var result = await resolver.ResolveAsync(DestinationType.FhirRepository, ResourceType, DestinationField, null, null, null, CancellationToken.None);

        result.Should().ContainSingle().Which.Should().Be(fieldSpecific);
    }

    [Fact]
    public async Task Returns_empty_when_nothing_is_configured_at_any_tier()
    {
        var repository = CreateRepositoryMock();
        var resolver = new EffectiveRuleResolver(repository.Object);

        var result = await resolver.ResolveAsync(DestinationType.FhirRepository, ResourceType, DestinationField, null, null, null, CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task A_source_specific_field_rule_beats_a_blanket_any_source_field_rule()
    {
        var repository = CreateRepositoryMock();
        var anySource = new TransformationRule(
            TransformScope.Field, TransformNodeType.ValueCodeMapping, "{}", resourceType: ResourceType, destinationField: DestinationField);
        var epicOnly = new TransformationRule(
            TransformScope.Field, TransformNodeType.StatusEnumCoercion, "{}",
            resourceType: ResourceType, destinationField: DestinationField, sourceSystem: "Epic");
        repository
            .Setup(x => x.GetFieldScopedAsync(ResourceType, DestinationField, "Epic", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[anySource, epicOnly]);

        var resolver = new EffectiveRuleResolver(repository.Object);
        var result = await resolver.ResolveAsync(
            DestinationType.SqlServer, ResourceType, DestinationField, null, "Epic", null, CancellationToken.None);

        result.Should().ContainSingle().Which.Should().Be(epicOnly);
    }

    [Fact]
    public async Task A_blanket_field_rule_still_applies_when_no_source_specific_row_matches()
    {
        var repository = CreateRepositoryMock();
        var anySource = new TransformationRule(
            TransformScope.Field, TransformNodeType.ValueCodeMapping, "{}", resourceType: ResourceType, destinationField: DestinationField);
        repository
            .Setup(x => x.GetFieldScopedAsync(ResourceType, DestinationField, "Healow", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[anySource]);

        var resolver = new EffectiveRuleResolver(repository.Object);
        var result = await resolver.ResolveAsync(
            DestinationType.SqlServer, ResourceType, DestinationField, null, "Healow", null, CancellationToken.None);

        result.Should().ContainSingle().Which.Should().Be(anySource);
    }

    [Fact]
    public async Task A_source_field_specific_rule_beats_a_blanket_any_field_rule()
    {
        var repository = CreateRepositoryMock();
        var anyField = new TransformationRule(
            TransformScope.Field, TransformNodeType.ValueCodeMapping, "{}", resourceType: ResourceType, destinationField: DestinationField);
        var identifierValueOnly = new TransformationRule(
            TransformScope.Field, TransformNodeType.IdentifierFormatting, "{}",
            resourceType: ResourceType, destinationField: DestinationField, sourceField: "identifier.value");
        repository
            .Setup(x => x.GetFieldScopedAsync(ResourceType, DestinationField, null, "identifier.value", It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[anyField, identifierValueOnly]);

        var resolver = new EffectiveRuleResolver(repository.Object);
        var result = await resolver.ResolveAsync(
            DestinationType.SqlServer, ResourceType, DestinationField, null, null, "identifier.value", CancellationToken.None);

        result.Should().ContainSingle().Which.Should().Be(identifierValueOnly);
    }
}
