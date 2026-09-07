using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Services.Transforms;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FluentAssertions;
using Moq;

namespace FHIRBridge.UnitTests.Transforms;

/// <summary>
/// FHIR transformation rules resolve at <see cref="TransformScope.Workflow"/> scope ONLY — no fall-through to
/// the broader tiers <see cref="EffectiveRuleResolver"/> walks for mapped columns.
///
/// That is the whole point of these tests. With fall-through, a rule authored while configuring one pipeline
/// silently transformed every other pipeline sharing the destination type, and deleting the rule you could see
/// changed nothing because another workflow's identical rule was still there. A transformation rule belongs to
/// the workflow it was authored in.
/// </summary>
public sealed class FhirResourceRuleResolverTests
{
    private static readonly Guid ThisWorkflow = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static TransformationRule Rule(
        TransformScope scope,
        string sourceField,
        TransformNodeType nodeType = TransformNodeType.StringNormalization,
        string? sourceSystem = null,
        bool enabled = true)
    {
        var rule = new TransformationRule(
            scope,
            nodeType,
            "{}",
            resourceType: scope is TransformScope.Global or TransformScope.DestinationType ? null : "Observation",
            resourcePipelineRouteId: scope == TransformScope.Workflow ? ThisWorkflow : null,
            sourceField: sourceField,
            sourceSystem: sourceSystem,
            executionPhase: TransformExecutionPhase.FhirResource);

        if (!enabled)
        {
            rule.SetEnabled(false);
        }

        return rule;
    }

    /// <summary>Stubs the repository per requested scope, so a test can prove a tier is never even asked for
    /// rather than only that its rows do not come back.</summary>
    private static (FhirResourceRuleResolver Sut, Mock<ITransformationRuleRepository> Repository) CreateSut(
        Func<TransformScope, IReadOnlyList<TransformationRule>> rulesByTier)
    {
        var repository = new Mock<ITransformationRuleRepository>();
        repository
            .Setup(r => r.GetFhirResourceRulesAsync(
                It.IsAny<TransformScope>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<DestinationType?>(),
                It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TransformScope scope, string _, string? sourceField, DestinationType? _, Guid? _,
                    string? _, CancellationToken _) =>
                rulesByTier(scope)
                    // Mirrors the repository's own "this exact path, or no path restriction" filter.
                    .Where(rule => sourceField is null || rule.SourceField == sourceField)
                    .ToList());

        return (new FhirResourceRuleResolver(repository.Object), repository);
    }

    private static Task<IReadOnlyList<TransformationRule>> ResolveAsync(
        FhirResourceRuleResolver sut, string sourceField = "Observation.status", Guid? routeId = null) =>
        sut.ResolveAsync("Observation", sourceField, DestinationType.Medplum, routeId ?? ThisWorkflow, "Epic", default);

    [Fact]
    public async Task Resolves_this_workflows_rule()
    {
        var (sut, _) = CreateSut(scope => scope == TransformScope.Workflow
            ? [Rule(TransformScope.Workflow, "Observation.status")]
            : []);

        var rules = await ResolveAsync(sut);

        rules.Should().ContainSingle().Which.Scope.Should().Be(TransformScope.Workflow);
    }

    [Fact]
    public async Task Never_consults_any_tier_other_than_workflow()
    {
        // The guarantee: a Field/ResourceType/DestinationType/Global rule for this same path cannot leak in,
        // because those tiers are not queried at all.
        // Stubbed the way the real repository behaves: a query for one scope only ever returns rows OF that
        // scope. So the ResourceType rule below exists, and simply never comes back.
        var (sut, repository) = CreateSut(scope => scope == TransformScope.ResourceType
            ? [Rule(TransformScope.ResourceType, "Observation.status")]
            : []);

        var rules = await ResolveAsync(sut);

        rules.Should().BeEmpty("only Workflow-scoped rows are eligible");

        foreach (var scope in new[]
                 {
                     TransformScope.Field, TransformScope.ResourceType,
                     TransformScope.DestinationType, TransformScope.Global,
                 })
        {
            repository.Verify(
                r => r.GetFhirResourceRulesAsync(
                    scope, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<DestinationType?>(),
                    It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
                Times.Never,
                $"the {scope} tier must never be queried");
        }
    }

    [Fact]
    public async Task Resolves_nothing_when_the_workflow_has_no_id_yet()
    {
        // A brand-new workflow has no id, so nothing can have been authored against it. The repository is not
        // touched at all — asking would be meaningless, not merely fruitless.
        var (sut, repository) = CreateSut(_ => [Rule(TransformScope.Workflow, "Observation.status")]);

        var rules = await sut.ResolveAsync(
            "Observation", "Observation.status", DestinationType.Medplum, null, "Epic", default);

        rules.Should().BeEmpty();
        repository.Verify(
            r => r.GetFhirResourceRulesAsync(
                It.IsAny<TransformScope>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<DestinationType?>(),
                It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task The_workflow_id_is_passed_to_the_repository()
    {
        // What keeps one workflow's rules out of another's resolution — the repository filters on it.
        var (sut, repository) = CreateSut(_ => []);

        await ResolveAsync(sut);

        repository.Verify(
            r => r.GetFhirResourceRulesAsync(
                TransformScope.Workflow, "Observation", "Observation.status", DestinationType.Medplum,
                ThisWorkflow, "Epic", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task A_disabled_rule_is_not_returned()
    {
        var (sut, _) = CreateSut(scope => scope == TransformScope.Workflow
            ? [Rule(TransformScope.Workflow, "Observation.status", enabled: false)]
            : []);

        (await ResolveAsync(sut)).Should().BeEmpty();
    }

    [Fact]
    public async Task Only_rules_naming_this_exact_path_are_returned()
    {
        var (sut, _) = CreateSut(scope => scope == TransformScope.Workflow
            ?
            [
                Rule(TransformScope.Workflow, "Observation.valueQuantity.value"),
                Rule(TransformScope.Workflow, "Observation.status", TransformNodeType.HashingMasking),
            ]
            : []);

        var rules = await ResolveAsync(sut);

        rules.Should().ContainSingle()
            .Which.NodeType.Should().Be(TransformNodeType.HashingMasking);
    }

    [Fact]
    public async Task A_source_specific_rule_beats_a_blanket_one_on_the_same_path()
    {
        // The source-system dimension still has blanket rows, so this preference survives — it is the only
        // "specific beats blanket" step left now that the scope chain is a single tier.
        var (sut, _) = CreateSut(scope => scope == TransformScope.Workflow
            ?
            [
                Rule(TransformScope.Workflow, "Observation.status"),
                Rule(TransformScope.Workflow, "Observation.status", TransformNodeType.HashingMasking, sourceSystem: "Epic"),
            ]
            : []);

        var rules = await ResolveAsync(sut);

        rules.Should().ContainSingle().Which.SourceSystem.Should().Be("Epic");
    }

    [Fact]
    public void A_rule_with_no_path_cannot_exist_for_this_phase()
    {
        // Why there is no "path-specific beats blanket" step: with no destination column, the read path IS the
        // rule's key, so a pathless rule is rejected at construction rather than matching every path.
        var create = () => new TransformationRule(
            TransformScope.Workflow,
            TransformNodeType.StringNormalization,
            "{}",
            resourceType: "Observation",
            resourcePipelineRouteId: ThisWorkflow,
            executionPhase: TransformExecutionPhase.FhirResource);

        create.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task Source_paths_come_only_from_this_workflows_rules()
    {
        var (sut, _) = CreateSut(scope => scope == TransformScope.Workflow
            ?
            [
                Rule(TransformScope.Workflow, "Observation.valueQuantity.value"),
                Rule(TransformScope.Workflow, "Observation.status"),
            ]
            : [Rule(TransformScope.ResourceType, "Observation.code.coding.code")]);

        var paths = await sut.ResolveSourceFieldsAsync(
            "Observation", DestinationType.Medplum, ThisWorkflow, "Epic", default);

        paths.Should().BeEquivalentTo(["Observation.valueQuantity.value", "Observation.status"]);
    }

    [Fact]
    public async Task No_source_paths_when_the_workflow_has_no_id_yet()
    {
        var (sut, _) = CreateSut(_ => [Rule(TransformScope.Workflow, "Observation.status")]);

        var paths = await sut.ResolveSourceFieldsAsync(
            "Observation", DestinationType.Medplum, null, "Epic", default);

        paths.Should().BeEmpty();
    }

    [Fact]
    public async Task Disabled_rules_contribute_no_source_paths()
    {
        var (sut, _) = CreateSut(scope => scope == TransformScope.Workflow
            ? [Rule(TransformScope.Workflow, "Observation.status", enabled: false)]
            : []);

        var paths = await sut.ResolveSourceFieldsAsync(
            "Observation", DestinationType.Medplum, ThisWorkflow, "Epic", default);

        paths.Should().BeEmpty();
    }
}
