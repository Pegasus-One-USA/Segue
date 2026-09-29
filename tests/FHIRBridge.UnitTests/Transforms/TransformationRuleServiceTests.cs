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

        var service = new TransformationRuleService(
            repository.Object, new EffectiveRuleResolver(repository.Object), CreateRegistry(), Mock.Of<IConfigurationRepository>());

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
            TransformScope.ResourceType, TransformNodeType.DateTimeFormat,
            JsonSerializer.Serialize(new Dictionary<string, string> { ["targetType"] = "date" }),
            resourceType: "Patient");
        repository
            .Setup(x => x.GetResourceTypeScopedAsync("Patient", "BirthDate", It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[rule]);
        SetupEmptyRepository(repository, exceptResourceType: true);

        var service = new TransformationRuleService(
            repository.Object, new EffectiveRuleResolver(repository.Object), CreateRegistry(), Mock.Of<IConfigurationRepository>());

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

        var service = new TransformationRuleService(
            repository.Object, new EffectiveRuleResolver(repository.Object), CreateRegistry(), Mock.Of<IConfigurationRepository>());

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
            repository.Object, new EffectiveRuleResolver(repository.Object), CreateRegistry(),
            Mock.Of<IConfigurationRepository>(), secretAccessor.Object);

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

        var service = new TransformationRuleService(
            repository.Object, new EffectiveRuleResolver(repository.Object), CreateRegistry(), Mock.Of<IConfigurationRepository>());

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

        var service = new TransformationRuleService(
            repository.Object, new EffectiveRuleResolver(repository.Object), CreateRegistry(), Mock.Of<IConfigurationRepository>());

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

        var service = new TransformationRuleService(
            repository.Object, new EffectiveRuleResolver(repository.Object), CreateRegistry(), Mock.Of<IConfigurationRepository>());

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
    public async Task SaveRuleAsync_updates_the_matching_rule_instead_of_cloning_it_when_no_id_is_supplied()
    {
        // The authoring UIs do not always have an id to send — a rule reopened from a list, a re-save after an
        // error. Without a natural-key match every such save appended a duplicate: four byte-identical
        // Patient.birthDate/DateMathAge rows were observed in a dev database, and deleting the one on screen
        // left the other three quietly in effect.
        var repository = new Mock<ITransformationRuleRepository>();
        var existing = new TransformationRule(
            TransformScope.Field,
            TransformNodeType.DateMathAge,
            "{\"operation\":\"age\"}",
            resourceType: "Patient",
            destinationField: "BirthDateAge",
            sourceField: "Patient.birthDate");
        StubNaturalKeyLookup(repository, existing);

        var service = new TransformationRuleService(
            repository.Object, new EffectiveRuleResolver(repository.Object), CreateRegistry(), Mock.Of<IConfigurationRepository>());

        var dto = await service.SaveRuleAsync(new SaveTransformationRuleRequest(
            Id: null,
            Scope: TransformScope.Field,
            NodeType: TransformNodeType.DateMathAge,
            Config: new Dictionary<string, string> { ["operation"] = "age", ["days"] = "30" },
            ResourceType: "Patient",
            DestinationField: "BirthDateAge",
            SourceField: "Patient.birthDate"));

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
            TransformScope.Field, TransformNodeType.DateMathAge, "{}",
            resourceType: "Patient", destinationField: "BirthDateAge", sourceField: "Patient.birthDate");
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
            Scope: TransformScope.Field,
            NodeType: TransformNodeType.DateMathAge,
            Config: new Dictionary<string, string>(),
            ResourceType: "Patient",
            DestinationField: "BirthDateAge",
            SourceField: "Patient.birthDate"));

        added.Should().NotBeNull();
        repository.Verify(
            x => x.UpdateAsync(It.IsAny<TransformationRule>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SaveRuleAsync_updates_in_place_when_an_existing_id_is_supplied()
    {
        var repository = new Mock<ITransformationRuleRepository>();
        var existing = new TransformationRule(TransformScope.Global, TransformNodeType.DefaultNullHandling, "{}");
        repository.Setup(x => x.GetByIdAsync(existing.Id, It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var service = new TransformationRuleService(
            repository.Object, new EffectiveRuleResolver(repository.Object), CreateRegistry(), Mock.Of<IConfigurationRepository>());

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

        var service = new TransformationRuleService(
            repository.Object, new EffectiveRuleResolver(repository.Object), CreateRegistry(), Mock.Of<IConfigurationRepository>());
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

/// <summary>
/// Saving an EDITED rule (id supplied) rewrote only what the rule does — its config, order and failure
/// handling — and silently kept the address it was originally authored against. The de-identification editor
/// made that plainest: resource and source field are nearly all it lets you change, so "Edit" returned 200,
/// the row came back unchanged, and the edit appeared to do nothing. Retarget is what persists the move.
/// </summary>
public sealed class TransformationRuleRetargetOnSaveTests
{
    private static readonly Guid ProfileId = Guid.NewGuid();

    private static TransformationRule DeIdRule(string resourceType, string sourceField, string mode) =>
        new(TransformScope.ResourceType, TransformNodeType.HashingMasking,
            JsonSerializer.Serialize(new Dictionary<string, string> { ["mode"] = mode }),
            resourceType: resourceType, sourceField: sourceField,
            executionPhase: TransformExecutionPhase.PreMapping, deIdentificationProfileId: ProfileId);

    private static (TransformationRuleService Service, Mock<ITransformationRuleRepository> Repository) BuildSut(
        TransformationRule existing)
    {
        var repository = new Mock<ITransformationRuleRepository>();
        repository.Setup(x => x.GetByIdAsync(existing.Id, It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        // The save path now checks the natural key for a collision before retargeting, so this has to answer.
        repository.Setup(x => x.ListAsync(
                It.IsAny<TransformScope>(), It.IsAny<DestinationType?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<TransformExecutionPhase?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[]);

        var service = new TransformationRuleService(
            repository.Object, new EffectiveRuleResolver(repository.Object),
            new TransformNodeRegistry([new HashingMaskingNode()]), Mock.Of<IConfigurationRepository>());

        return (service, repository);
    }

    private static SaveTransformationRuleRequest EditTo(
        TransformationRule existing, string resourceType, string sourceField, string mode) =>
        new(existing.Id, TransformScope.ResourceType, TransformNodeType.HashingMasking,
            new Dictionary<string, string> { ["mode"] = mode },
            ResourceType: resourceType,
            SourceField: sourceField,
            ExecutionPhase: TransformExecutionPhase.PreMapping,
            DeIdentificationProfileId: ProfileId);

    [Fact]
    public async Task Editing_a_rules_source_field_persists_the_new_field()
    {
        var existing = DeIdRule("Patient", "$.gender", "redact");
        var (service, repository) = BuildSut(existing);

        var saved = await service.SaveRuleAsync(EditTo(existing, "Patient", "$.birthDate", "redact"));

        saved.SourceField.Should().Be("$.birthDate");
        existing.SourceField.Should().Be("$.birthDate", "the stored entity is what the next run reads");
        repository.Verify(x => x.UpdateAsync(existing, It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(x => x.AddAsync(It.IsAny<TransformationRule>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Editing_a_rules_resource_type_persists_the_new_resource()
    {
        var existing = DeIdRule("Patient", "$.id", "hash");
        var (service, _) = BuildSut(existing);

        var saved = await service.SaveRuleAsync(EditTo(existing, "Encounter", "$.id", "hash"));

        saved.ResourceType.Should().Be("Encounter");
        existing.ResourceType.Should().Be("Encounter");
    }

    [Fact]
    public async Task Editing_only_the_config_still_leaves_the_address_alone()
    {
        var existing = DeIdRule("Patient", "$.gender", "redact");
        var (service, _) = BuildSut(existing);

        var saved = await service.SaveRuleAsync(EditTo(existing, "Patient", "$.gender", "hash"));

        saved.Config["mode"].Should().Be("hash");
        existing.ResourceType.Should().Be("Patient");
        existing.SourceField.Should().Be("$.gender");
    }

    /// <summary>
    /// Retargeting onto an address another rule already holds leaves two rules sharing a natural key. That
    /// permanently defeats FindByNaturalKeyAsync's `matches.Count == 1` guard, so every later id-less save
    /// inserts another clone rather than updating one — the duplicate multiplies, and deleting the row you can
    /// see leaves the rest running.
    /// </summary>
    [Fact]
    public async Task An_edit_cannot_move_a_rule_onto_an_address_another_rule_already_holds()
    {
        var edited = DeIdRule("Patient", "$.gender", "redact");
        var occupant = DeIdRule("Patient", "$.birthDate", "redact");

        var repository = new Mock<ITransformationRuleRepository>();
        repository.Setup(x => x.GetByIdAsync(edited.Id, It.IsAny<CancellationToken>())).ReturnsAsync(edited);
        repository.Setup(x => x.ListAsync(
                It.IsAny<TransformScope>(), It.IsAny<DestinationType?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<TransformExecutionPhase?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[occupant]);

        var service = new TransformationRuleService(
            repository.Object, new EffectiveRuleResolver(repository.Object),
            new TransformNodeRegistry([new HashingMaskingNode()]), Mock.Of<IConfigurationRepository>());

        var act = async () => await service.SaveRuleAsync(EditTo(edited, "Patient", "$.birthDate", "redact"));

        await act.Should().ThrowAsync<InvalidOperationException>();
        edited.SourceField.Should().Be("$.gender", "the rejected edit must not have been applied");
        repository.Verify(x => x.UpdateAsync(It.IsAny<TransformationRule>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// The address is ALREADY duplicated. FindByNaturalKeyAsync returns null here (it refuses to choose between
    /// duplicates), so using it as the collision test would read "free" for the most crowded address there is
    /// and let a move add a third copy.
    /// </summary>
    [Fact]
    public async Task An_edit_is_rejected_even_when_the_target_address_is_already_duplicated()
    {
        var edited = DeIdRule("Patient", "$.gender", "redact");
        var occupantA = DeIdRule("Patient", "$.birthDate", "redact");
        var occupantB = DeIdRule("Patient", "$.birthDate", "hash");

        var repository = new Mock<ITransformationRuleRepository>();
        repository.Setup(x => x.GetByIdAsync(edited.Id, It.IsAny<CancellationToken>())).ReturnsAsync(edited);
        repository.Setup(x => x.ListAsync(
                It.IsAny<TransformScope>(), It.IsAny<DestinationType?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<TransformExecutionPhase?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[occupantA, occupantB]);

        var service = new TransformationRuleService(
            repository.Object, new EffectiveRuleResolver(repository.Object),
            new TransformNodeRegistry([new HashingMaskingNode()]), Mock.Of<IConfigurationRepository>());

        var act = async () => await service.SaveRuleAsync(EditTo(edited, "Patient", "$.birthDate", "redact"));

        await act.Should().ThrowAsync<InvalidOperationException>();
        edited.SourceField.Should().Be("$.gender");
    }

    /// <summary>
    /// ListAsync treats a null argument as "no filter", and a de-identification rule has null DestinationField,
    /// DestinationType, SourceSystem and route id — so the repository hands back a SUPERSET of the address.
    /// Rejecting on any returned row would block a perfectly legal edit because some unrelated rule happened to
    /// survive the loose query.
    /// </summary>
    [Fact]
    public async Task A_rule_that_only_matches_the_loose_query_is_not_treated_as_a_collision()
    {
        var edited = DeIdRule("Patient", "$.gender", "redact");
        // Same node type and same profile, but a DIFFERENT source field — not the address being moved onto.
        var unrelated = DeIdRule("Patient", "$.name[*].family", "mask");

        var repository = new Mock<ITransformationRuleRepository>();
        repository.Setup(x => x.GetByIdAsync(edited.Id, It.IsAny<CancellationToken>())).ReturnsAsync(edited);
        repository.Setup(x => x.ListAsync(
                It.IsAny<TransformScope>(), It.IsAny<DestinationType?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<TransformExecutionPhase?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[unrelated]);

        var service = new TransformationRuleService(
            repository.Object, new EffectiveRuleResolver(repository.Object),
            new TransformNodeRegistry([new HashingMaskingNode()]), Mock.Of<IConfigurationRepository>());

        var saved = await service.SaveRuleAsync(EditTo(edited, "Patient", "$.birthDate", "redact"));

        saved.SourceField.Should().Be("$.birthDate", "nothing actually occupies that address");
        edited.SourceField.Should().Be("$.birthDate");
    }

    [Fact]
    public async Task An_edit_that_leaves_the_rule_where_it_is_is_not_a_collision_with_itself()
    {
        // The rule's own row comes back from the natural-key lookup; matching on it must not block a plain
        // config-only edit.
        var existing = DeIdRule("Patient", "$.gender", "redact");

        var repository = new Mock<ITransformationRuleRepository>();
        repository.Setup(x => x.GetByIdAsync(existing.Id, It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        repository.Setup(x => x.ListAsync(
                It.IsAny<TransformScope>(), It.IsAny<DestinationType?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<TransformExecutionPhase?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[existing]);

        var service = new TransformationRuleService(
            repository.Object, new EffectiveRuleResolver(repository.Object),
            new TransformNodeRegistry([new HashingMaskingNode()]), Mock.Of<IConfigurationRepository>());

        var saved = await service.SaveRuleAsync(EditTo(existing, "Patient", "$.gender", "hash"));

        saved.Config["mode"].Should().Be("hash");
    }

    [Fact]
    public async Task An_edit_cannot_move_a_pre_mapping_rule_to_a_scope_it_may_not_hold()
    {
        // Retarget must enforce the same invariant the constructor does, or an edit becomes a way around it.
        var existing = DeIdRule("Patient", "$.gender", "redact");
        var (service, _) = BuildSut(existing);

        var request = EditTo(existing, "Patient", "$.gender", "redact") with { Scope = TransformScope.Field };
        var act = async () => await service.SaveRuleAsync(request);

        await act.Should().ThrowAsync<ArgumentException>();
    }
}
