using System.Text.Json;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
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
        new DefaultNullHandlingNode(),
        new HashingMaskingNode(),
        new UnitConversionNode()
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
    public async Task GetEffectiveRulesAsync_returns_the_real_editable_rule_not_just_a_trace()
    {
        var repository = new Mock<ITransformationRuleRepository>();
        var rule = new TransformationRule(
            TransformScope.ResourceType, TransformNodeType.DateTimeFormat,
            JsonSerializer.Serialize(new Dictionary<string, string> { ["targetType"] = "date" }),
            resourceType: "Patient");
        repository
            .Setup(x => x.GetResourceTypeScopedAsync("Patient", "BirthDate", It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[rule]);
        SetupEmptyRepository(repository, exceptResourceType: true);

        var service = new TransformationRuleService(repository.Object, new EffectiveRuleResolver(repository.Object), CreateRegistry());

        var rules = await service.GetEffectiveRulesAsync(DestinationType.SqlServer, "Patient", "BirthDate", null, null, null);

        rules.Should().ContainSingle();
        rules[0].Id.Should().Be(rule.Id);
        rules[0].Config["targetType"].Should().Be("date");
    }

    [Fact]
    public async Task PreviewAsync_substitutes_the_configured_default_instead_of_running_the_node_when_input_is_missing()
    {
        var repository = new Mock<ITransformationRuleRepository>();
        var rule = new TransformationRule(
            TransformScope.ResourceType, TransformNodeType.DateTimeFormat,
            JsonSerializer.Serialize(new Dictionary<string, string> { ["targetType"] = "date" }),
            resourceType: "Patient", order: 0, onNull: NullPolicy.Default, onNullDefaultValue: "1900-01-01");
        repository
            .Setup(x => x.GetResourceTypeScopedAsync("Patient", "BirthDate", It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[rule]);
        SetupEmptyRepository(repository, exceptResourceType: true);

        var service = new TransformationRuleService(repository.Object, new EffectiveRuleResolver(repository.Object), CreateRegistry());

        var result = await service.PreviewAsync(new TransformPreviewRequest(DestinationType.SqlServer, "Patient", "BirthDate", null));

        result.FinalValue.Should().Be("1900-01-01", "the node must not run at all when OnNull is Default — the default value IS the output");
    }

    [Fact]
    public async Task PreviewAsync_resolves_the_hash_key_via_the_secret_accessor_for_HashingMasking_rules()
    {
        var repository = new Mock<ITransformationRuleRepository>();
        var rule = new TransformationRule(
            TransformScope.Global, TransformNodeType.HashingMasking,
            JsonSerializer.Serialize(new Dictionary<string, string> { ["mode"] = "hash" }));
        SetupEmptyRepository(repository);
        repository
            .Setup(x => x.GetGlobalScopedAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[rule]);

        var secretAccessor = new Mock<IAppSecretAccessor>();
        secretAccessor.SetupGet(x => x.TransformHashingKey).Returns("vault-secret");

        var service = new TransformationRuleService(
            repository.Object, new EffectiveRuleResolver(repository.Object), CreateRegistry(), secretAccessor.Object);

        var result = await service.PreviewAsync(new TransformPreviewRequest(DestinationType.SqlServer, "Patient", "MRN", "A12345"));

        result.Steps.Should().ContainSingle().Which.Success.Should().BeTrue("the accessor must supply the hash key so the node doesn't fail for lack of a secret");
    }

    [Fact]
    public async Task A_field_rule_with_no_resource_type_resolves_for_any_resource_type()
    {
        var repository = new Mock<ITransformationRuleRepository>();
        var rule = new TransformationRule(
            TransformScope.Field, TransformNodeType.DateTimeFormat, JsonSerializer.Serialize(new Dictionary<string, string> { ["targetType"] = "date" }),
            resourceType: null, destinationField: null, sourceField: "birthDate");
        SetupEmptyRepository(repository);
        repository
            .Setup(x => x.GetFieldScopedAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), "birthDate", It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[rule]);

        var service = new TransformationRuleService(repository.Object, new EffectiveRuleResolver(repository.Object), CreateRegistry());

        var result = await service.PreviewAsync(new TransformPreviewRequest(
            DestinationType.SqlServer, "Practitioner", "DOB", "03/14/2026", SourceField: "birthDate"));

        result.FinalValue.Should().Be("2026-03-14");
        result.EffectiveScope.Should().Be(TransformScope.Field);
    }

    [Fact]
    public async Task A_resource_and_field_specific_rule_wins_over_a_blanket_source_field_rule_at_the_same_tier()
    {
        var repository = new Mock<ITransformationRuleRepository>();
        var blanket = new TransformationRule(
            TransformScope.Field, TransformNodeType.DateTimeFormat, JsonSerializer.Serialize(new Dictionary<string, string> { ["targetType"] = "date" }),
            resourceType: null, destinationField: null, sourceField: "birthDate");
        var specific = new TransformationRule(
            TransformScope.Field, TransformNodeType.DefaultNullHandling, JsonSerializer.Serialize(new Dictionary<string, string> { ["default"] = "unknown" }),
            resourceType: "Patient", destinationField: "BirthDate", sourceField: "birthDate");
        SetupEmptyRepository(repository);
        repository
            .Setup(x => x.GetFieldScopedAsync("Patient", "BirthDate", It.IsAny<string?>(), "birthDate", It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[blanket, specific]);

        var service = new TransformationRuleService(repository.Object, new EffectiveRuleResolver(repository.Object), CreateRegistry());

        var result = await service.PreviewAsync(new TransformPreviewRequest(
            DestinationType.SqlServer, "Patient", "BirthDate", null, SourceField: "birthDate"));

        result.Steps.Should().ContainSingle().Which.NodeType.Should().Be(TransformNodeType.DefaultNullHandling);
    }

    [Theory]
    [InlineData(DestinationType.FhirRepository, true)]
    [InlineData(DestinationType.SqlServer, false)]
    public async Task PreviewAsync_automatically_passes_the_real_destination_type_to_the_node(
        DestinationType destinationType, bool expectFullQuantityObject)
    {
        var repository = new Mock<ITransformationRuleRepository>();
        var rule = new TransformationRule(
            TransformScope.Global, TransformNodeType.UnitConversion,
            JsonSerializer.Serialize(new Dictionary<string, string> { ["sourceUnit"] = "Cel", ["targetUnit"] = "[degF]", ["precision"] = "1" }));
        SetupEmptyRepository(repository);
        repository
            .Setup(x => x.GetGlobalScopedAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[rule]);

        var service = new TransformationRuleService(repository.Object, new EffectiveRuleResolver(repository.Object), CreateRegistry());

        var result = await service.PreviewAsync(new TransformPreviewRequest(destinationType, "Observation", "Value", 38.9m));

        if (expectFullQuantityObject)
        {
            result.FinalValue.Should().BeOfType<System.Text.Json.Nodes.JsonObject>(
                "a FHIR-native destination should get the real Quantity structure, without the caller asking for it explicitly");
        }
        else
        {
            result.FinalValue.Should().Be(102.0m, "a flat destination should get just the number, without the caller asking for it explicitly");
        }
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

    private static void SetupEmptyRepository(Mock<ITransformationRuleRepository> repository, bool exceptResourceType = false)
    {
        repository.Setup(x => x.GetWorkflowScopedAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[]);
        repository.Setup(x => x.GetFieldScopedAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[]);
        if (!exceptResourceType)
        {
            repository.Setup(x => x.GetResourceTypeScopedAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyList<TransformationRule>)[]);
        }

        repository.Setup(x => x.GetDestinationTypeScopedAsync(It.IsAny<DestinationType>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[]);
        repository.Setup(x => x.GetGlobalScopedAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[]);
    }
}
