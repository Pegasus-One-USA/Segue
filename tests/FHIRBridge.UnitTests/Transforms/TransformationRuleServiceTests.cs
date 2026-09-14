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
    private static readonly Guid WorkflowId = Guid.NewGuid();

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
            TransformScope.Workflow, TransformNodeType.DateTimeFormat,
            JsonSerializer.Serialize(new Dictionary<string, string> { ["targetType"] = "date" }),
            resourceType: "Patient", destinationField: "BirthDate", resourcePipelineRouteId: WorkflowId);
        SetupEmptyRepository(repository);
        repository
            .Setup(x => x.GetWorkflowScopedAsync(WorkflowId, "Patient", "BirthDate", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[rule]);

        var service = new TransformationRuleService(
            repository.Object, new EffectiveRuleResolver(repository.Object), CreateRegistry(), Mock.Of<IConfigurationRepository>());

        var result = await service.PreviewAsync(new TransformPreviewRequest(
            DestinationType.SqlServer, "Patient", "BirthDate", "03/14/2026", ResourcePipelineRouteId: WorkflowId));

        result.FinalValue.Should().Be("2026-03-14");
        result.EffectiveScope.Should().Be(TransformScope.Workflow);
        result.Steps.Should().ContainSingle().Which.Success.Should().BeTrue();
    }

    [Fact]
    public async Task PreviewAsync_is_a_passthrough_when_no_rule_applies_anywhere()
    {
        var repository = new Mock<ITransformationRuleRepository>();
        SetupEmptyRepository(repository);

        var service = new TransformationRuleService(
            repository.Object, new EffectiveRuleResolver(repository.Object), CreateRegistry(), Mock.Of<IConfigurationRepository>());

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
            TransformScope.Workflow, TransformNodeType.DateTimeFormat,
            JsonSerializer.Serialize(new Dictionary<string, string> { ["targetType"] = "date" }),
            resourceType: "Patient", destinationField: "BirthDate", resourcePipelineRouteId: WorkflowId);
        SetupEmptyRepository(repository);
        repository
            .Setup(x => x.GetWorkflowScopedAsync(WorkflowId, "Patient", "BirthDate", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[rule]);

        var service = new TransformationRuleService(
            repository.Object, new EffectiveRuleResolver(repository.Object), CreateRegistry(), Mock.Of<IConfigurationRepository>());

        var rules = await service.GetEffectiveRulesAsync(DestinationType.SqlServer, "Patient", "BirthDate", WorkflowId, null, null);

        rules.Should().ContainSingle();
        rules[0].Id.Should().Be(rule.Id);
        rules[0].Config["targetType"].Should().Be("date");
    }

    [Fact]
    public async Task PreviewAsync_substitutes_the_configured_default_instead_of_running_the_node_when_input_is_missing()
    {
        var repository = new Mock<ITransformationRuleRepository>();
        var rule = new TransformationRule(
            TransformScope.Workflow, TransformNodeType.DateTimeFormat,
            JsonSerializer.Serialize(new Dictionary<string, string> { ["targetType"] = "date" }),
            resourceType: "Patient", destinationField: "BirthDate", resourcePipelineRouteId: WorkflowId,
            order: 0, onNull: NullPolicy.Default, onNullDefaultValue: "1900-01-01");
        SetupEmptyRepository(repository);
        repository
            .Setup(x => x.GetWorkflowScopedAsync(WorkflowId, "Patient", "BirthDate", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[rule]);

        var service = new TransformationRuleService(
            repository.Object, new EffectiveRuleResolver(repository.Object), CreateRegistry(), Mock.Of<IConfigurationRepository>());

        var result = await service.PreviewAsync(new TransformPreviewRequest(
            DestinationType.SqlServer, "Patient", "BirthDate", null, ResourcePipelineRouteId: WorkflowId));

        result.FinalValue.Should().Be("1900-01-01", "the node must not run at all when OnNull is Default — the default value IS the output");
    }

    [Fact]
    public async Task PreviewAsync_resolves_the_hash_key_via_the_secret_accessor_for_HashingMasking_rules()
    {
        var repository = new Mock<ITransformationRuleRepository>();
        var rule = new TransformationRule(
            TransformScope.Workflow, TransformNodeType.HashingMasking,
            JsonSerializer.Serialize(new Dictionary<string, string> { ["mode"] = "hash" }),
            resourceType: "Patient", destinationField: "MRN", resourcePipelineRouteId: WorkflowId);
        SetupEmptyRepository(repository);
        repository
            .Setup(x => x.GetWorkflowScopedAsync(WorkflowId, "Patient", "MRN", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[rule]);

        var secretAccessor = new Mock<IAppSecretAccessor>();
        secretAccessor.SetupGet(x => x.TransformHashingKey).Returns("vault-secret");

        var service = new TransformationRuleService(
            repository.Object, new EffectiveRuleResolver(repository.Object), CreateRegistry(),
            Mock.Of<IConfigurationRepository>(), secretAccessor.Object);

        var result = await service.PreviewAsync(new TransformPreviewRequest(
            DestinationType.SqlServer, "Patient", "MRN", "A12345", ResourcePipelineRouteId: WorkflowId));

        result.Steps.Should().ContainSingle().Which.Success.Should().BeTrue("the accessor must supply the hash key so the node doesn't fail for lack of a secret");
    }

    [Fact]
    public async Task A_source_specific_workflow_rule_wins_over_a_blanket_any_source_rule_at_the_same_field()
    {
        var repository = new Mock<ITransformationRuleRepository>();
        var anySource = new TransformationRule(
            TransformScope.Workflow, TransformNodeType.DateTimeFormat,
            JsonSerializer.Serialize(new Dictionary<string, string> { ["targetType"] = "date" }),
            resourceType: "Patient", destinationField: "BirthDate", resourcePipelineRouteId: WorkflowId);
        var epicOnly = new TransformationRule(
            TransformScope.Workflow, TransformNodeType.DefaultNullHandling,
            JsonSerializer.Serialize(new Dictionary<string, string> { ["default"] = "unknown" }),
            resourceType: "Patient", destinationField: "BirthDate", resourcePipelineRouteId: WorkflowId,
            sourceSystem: "Epic");
        SetupEmptyRepository(repository);
        repository
            .Setup(x => x.GetWorkflowScopedAsync(WorkflowId, "Patient", "BirthDate", "Epic", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[anySource, epicOnly]);

        var service = new TransformationRuleService(
            repository.Object, new EffectiveRuleResolver(repository.Object), CreateRegistry(), Mock.Of<IConfigurationRepository>());

        var result = await service.PreviewAsync(new TransformPreviewRequest(
            DestinationType.SqlServer, "Patient", "BirthDate", "03/14/2026",
            ResourcePipelineRouteId: WorkflowId, SourceSystem: "Epic"));

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
            TransformScope.Workflow, TransformNodeType.UnitConversion,
            JsonSerializer.Serialize(new Dictionary<string, string> { ["sourceUnit"] = "Cel", ["targetUnit"] = "[degF]", ["precision"] = "1" }),
            resourceType: "Observation", destinationField: "Value", resourcePipelineRouteId: WorkflowId);
        SetupEmptyRepository(repository);
        repository
            .Setup(x => x.GetWorkflowScopedAsync(WorkflowId, "Observation", "Value", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[rule]);

        var service = new TransformationRuleService(
            repository.Object, new EffectiveRuleResolver(repository.Object), CreateRegistry(), Mock.Of<IConfigurationRepository>());

        var result = await service.PreviewAsync(new TransformPreviewRequest(
            destinationType, "Observation", "Value", 38.9m, ResourcePipelineRouteId: WorkflowId));

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

    /// <summary>A save with no id now looks for an existing rule with the same natural key before inserting
    /// (see TransformationRuleService.FindByNaturalKeyAsync), so every such test has to stub that lookup.
    /// Returns nothing by default — "this really is a new rule".</summary>
    private static void StubNaturalKeyLookup(
        Mock<ITransformationRuleRepository> repository, params TransformationRule[] matches) =>
        repository
            .Setup(x => x.ListAsync(
                It.IsAny<TransformScope?>(), It.IsAny<DestinationType?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<TransformExecutionPhase?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(matches);

    [Fact]
    public async Task SaveRuleAsync_creates_a_new_rule_when_no_id_is_supplied()
    {
        var repository = new Mock<ITransformationRuleRepository>();
        StubNaturalKeyLookup(repository);
        TransformationRule? added = null;
        repository
            .Setup(x => x.AddAsync(It.IsAny<TransformationRule>(), It.IsAny<CancellationToken>()))
            .Callback<TransformationRule, CancellationToken>((r, _) => added = r)
            .Returns(Task.CompletedTask);

        var service = new TransformationRuleService(
            repository.Object, new EffectiveRuleResolver(repository.Object), CreateRegistry(), Mock.Of<IConfigurationRepository>());

        var dto = await service.SaveRuleAsync(new SaveTransformationRuleRequest(
            Id: null,
            Scope: TransformScope.Workflow,
            NodeType: TransformNodeType.StringNormalization,
            Config: new Dictionary<string, string> { ["case"] = "upper" },
            ResourceType: "Patient",
            DestinationField: "FamilyName",
            ResourcePipelineRouteId: WorkflowId));

        added.Should().NotBeNull();
        added!.DestinationField.Should().Be("FamilyName");
        dto.Config["case"].Should().Be("upper");
    }

    [Fact]
    public async Task SaveRuleAsync_updates_the_matching_rule_instead_of_cloning_it_when_no_id_is_supplied()
    {
        // The authoring UIs do not always have an id to send — a rule reopened from a list, a re-save after an
        // error. Without a natural-key match every such save appended a duplicate: four byte-identical
        // Patient.birthDate/DateMathAge rows were observed in a dev database, and deleting the one on screen
        // left the other three quietly in effect.
        var repository = new Mock<ITransformationRuleRepository>();
        var existing = new TransformationRule(
            TransformScope.Workflow,
            TransformNodeType.DateMathAge,
            "{\"operation\":\"age\"}",
            resourceType: "Patient",
            destinationField: "BirthDateAge",
            sourceField: "Patient.birthDate",
            resourcePipelineRouteId: WorkflowId);
        StubNaturalKeyLookup(repository, existing);

        var service = new TransformationRuleService(
            repository.Object, new EffectiveRuleResolver(repository.Object), CreateRegistry(), Mock.Of<IConfigurationRepository>());

        var dto = await service.SaveRuleAsync(new SaveTransformationRuleRequest(
            Id: null,
            Scope: TransformScope.Workflow,
            NodeType: TransformNodeType.DateMathAge,
            Config: new Dictionary<string, string> { ["operation"] = "age", ["days"] = "30" },
            ResourceType: "Patient",
            DestinationField: "BirthDateAge",
            SourceField: "Patient.birthDate",
            ResourcePipelineRouteId: WorkflowId));

        dto.Id.Should().Be(existing.Id, "the same rule was saved again, not a second one created");
        repository.Verify(
            x => x.AddAsync(It.IsAny<TransformationRule>(), It.IsAny<CancellationToken>()), Times.Never);
        repository.Verify(
            x => x.UpdateAsync(existing, It.IsAny<CancellationToken>()), Times.Once);
        existing.ConfigJson.Should().Contain("30", "the incoming config replaces the stored one");
    }

    [Fact]
    public async Task SaveRuleAsync_creates_a_new_rule_when_more_than_one_row_already_matches()
    {
        // Pre-existing duplicates are left alone rather than one being picked arbitrarily — a save must never
        // silently rewrite a row the caller did not name.
        var repository = new Mock<ITransformationRuleRepository>();
        TransformationRule Duplicate() => new(
            TransformScope.Workflow, TransformNodeType.DateMathAge, "{}",
            resourceType: "Patient", destinationField: "BirthDateAge", sourceField: "Patient.birthDate",
            resourcePipelineRouteId: WorkflowId);
        StubNaturalKeyLookup(repository, Duplicate(), Duplicate());

        TransformationRule? added = null;
        repository
            .Setup(x => x.AddAsync(It.IsAny<TransformationRule>(), It.IsAny<CancellationToken>()))
            .Callback<TransformationRule, CancellationToken>((r, _) => added = r)
            .Returns(Task.CompletedTask);

        var service = new TransformationRuleService(
            repository.Object, new EffectiveRuleResolver(repository.Object), CreateRegistry(), Mock.Of<IConfigurationRepository>());

        await service.SaveRuleAsync(new SaveTransformationRuleRequest(
            Id: null,
            Scope: TransformScope.Workflow,
            NodeType: TransformNodeType.DateMathAge,
            Config: new Dictionary<string, string>(),
            ResourceType: "Patient",
            DestinationField: "BirthDateAge",
            SourceField: "Patient.birthDate",
            ResourcePipelineRouteId: WorkflowId));

        added.Should().NotBeNull();
        repository.Verify(
            x => x.UpdateAsync(It.IsAny<TransformationRule>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SaveRuleAsync_updates_in_place_when_an_existing_id_is_supplied()
    {
        var repository = new Mock<ITransformationRuleRepository>();
        var existing = new TransformationRule(
            TransformScope.Workflow, TransformNodeType.DefaultNullHandling, "{}",
            resourceType: "Patient", destinationField: "BirthDate", resourcePipelineRouteId: WorkflowId);
        repository.Setup(x => x.GetByIdAsync(existing.Id, It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var service = new TransformationRuleService(
            repository.Object, new EffectiveRuleResolver(repository.Object), CreateRegistry(), Mock.Of<IConfigurationRepository>());

        await service.SaveRuleAsync(new SaveTransformationRuleRequest(
            Id: existing.Id,
            Scope: TransformScope.Workflow,
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
        var existing = new TransformationRule(
            TransformScope.Workflow, TransformNodeType.DefaultNullHandling, "{}",
            resourceType: "Patient", destinationField: "BirthDate", resourcePipelineRouteId: WorkflowId);
        repository.Setup(x => x.GetByIdAsync(existing.Id, It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var service = new TransformationRuleService(
            repository.Object, new EffectiveRuleResolver(repository.Object), CreateRegistry(), Mock.Of<IConfigurationRepository>());
        await service.DeleteRuleAsync(existing.Id);

        repository.Verify(x => x.DeleteAsync(existing, It.IsAny<CancellationToken>()), Times.Once);
    }

    private static void SetupEmptyRepository(Mock<ITransformationRuleRepository> repository)
    {
        repository.Setup(x => x.GetWorkflowScopedAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[]);
        repository.Setup(x => x.GetPendingWorkflowScopedAsync(It.IsAny<DestinationType>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[]);
    }
}
