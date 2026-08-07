using System.Text.Json;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs.Transforms;
using FHIRBridge.Application.Services.Transforms;
using FHIRBridge.Application.Services.Transforms.Nodes;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FluentAssertions;
using Moq;

namespace FHIRBridge.UnitTests.Transforms;

public sealed class TransformationRuleServiceTests
{
    private static ITransformNodeRegistry CreateRegistry() => new TransformNodeRegistry(
    [
        new DateTimeFormatNode(),
        new DefaultNullHandlingNode()
    ]);

    [Fact]
    public async Task PreviewAsync_resolves_the_effective_rule_and_applies_it_to_the_sample_value()
    {
        var repository = new Mock<ITransformationRuleRepository>();
        var rule = new TransformationRule(
            TransformScope.ResourceType, TransformNodeType.DateTimeFormat,
            JsonSerializer.Serialize(new Dictionary<string, string> { ["targetType"] = "date" }),
            resourceType: "Patient");
        repository
            .Setup(x => x.GetResourceTypeScopedAsync("Patient", "BirthDate", It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[rule]);
        repository
            .Setup(x => x.GetFieldScopedAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[]);
        repository
            .Setup(x => x.GetWorkflowScopedAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[]);

        var service = new TransformationRuleService(repository.Object, new EffectiveRuleResolver(repository.Object), CreateRegistry());

        var result = await service.PreviewAsync(new TransformPreviewRequest(
            DestinationType.SqlServer, "Patient", "BirthDate", "03/14/2026"));

        result.FinalValue.Should().Be("2026-03-14");
        result.EffectiveScope.Should().Be(TransformScope.ResourceType);
        result.Steps.Should().ContainSingle().Which.Success.Should().BeTrue();
    }

    [Fact]
    public async Task PreviewAsync_is_a_passthrough_when_no_rule_applies_anywhere()
    {
        var repository = new Mock<ITransformationRuleRepository>();
        SetupEmptyRepository(repository);

        var service = new TransformationRuleService(repository.Object, new EffectiveRuleResolver(repository.Object), CreateRegistry());

        var result = await service.PreviewAsync(new TransformPreviewRequest(
            DestinationType.SqlServer, "Patient", "SomeUnconfiguredField", "raw-value"));

        result.FinalValue.Should().Be("raw-value");
        result.EffectiveScope.Should().BeNull();
        result.Steps.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveRuleAsync_creates_a_new_rule_when_no_id_is_supplied()
    {
        var repository = new Mock<ITransformationRuleRepository>();
        TransformationRule? added = null;
        repository
            .Setup(x => x.AddAsync(It.IsAny<TransformationRule>(), It.IsAny<CancellationToken>()))
            .Callback<TransformationRule, CancellationToken>((r, _) => added = r)
            .Returns(Task.CompletedTask);

        var service = new TransformationRuleService(repository.Object, new EffectiveRuleResolver(repository.Object), CreateRegistry());

        var dto = await service.SaveRuleAsync(new SaveTransformationRuleRequest(
            Id: null,
            Scope: TransformScope.Field,
            NodeType: TransformNodeType.StringNormalization,
            Config: new Dictionary<string, string> { ["case"] = "upper" },
            ResourceType: "Patient",
            DestinationField: "FamilyName"));

        added.Should().NotBeNull();
        added!.DestinationField.Should().Be("FamilyName");
        dto.Config["case"].Should().Be("upper");
    }

    [Fact]
    public async Task SaveRuleAsync_updates_in_place_when_an_existing_id_is_supplied()
    {
        var repository = new Mock<ITransformationRuleRepository>();
        var existing = new TransformationRule(TransformScope.Global, TransformNodeType.DefaultNullHandling, "{}");
        repository.Setup(x => x.GetByIdAsync(existing.Id, It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var service = new TransformationRuleService(repository.Object, new EffectiveRuleResolver(repository.Object), CreateRegistry());

        await service.SaveRuleAsync(new SaveTransformationRuleRequest(
            Id: existing.Id,
            Scope: TransformScope.Global,
            NodeType: TransformNodeType.DefaultNullHandling,
            Config: new Dictionary<string, string> { ["default"] = "unknown" },
            Order: 5));

        existing.Order.Should().Be(5);
        repository.Verify(x => x.UpdateAsync(existing, It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(x => x.AddAsync(It.IsAny<TransformationRule>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteRuleAsync_removes_the_rule_when_found()
    {
        var repository = new Mock<ITransformationRuleRepository>();
        var existing = new TransformationRule(TransformScope.Global, TransformNodeType.DefaultNullHandling, "{}");
        repository.Setup(x => x.GetByIdAsync(existing.Id, It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var service = new TransformationRuleService(repository.Object, new EffectiveRuleResolver(repository.Object), CreateRegistry());
        await service.DeleteRuleAsync(existing.Id);

        repository.Verify(x => x.DeleteAsync(existing, It.IsAny<CancellationToken>()), Times.Once);
    }

    private static void SetupEmptyRepository(Mock<ITransformationRuleRepository> repository)
    {
        repository.Setup(x => x.GetWorkflowScopedAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[]);
        repository.Setup(x => x.GetFieldScopedAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[]);
        repository.Setup(x => x.GetResourceTypeScopedAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[]);
        repository.Setup(x => x.GetDestinationTypeScopedAsync(It.IsAny<DestinationType>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[]);
        repository.Setup(x => x.GetGlobalScopedAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[]);
    }
}
