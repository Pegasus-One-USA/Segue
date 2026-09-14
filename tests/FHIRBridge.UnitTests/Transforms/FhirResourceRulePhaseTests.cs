using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.UnitTests.Transforms;

/// <summary>
/// Covers <see cref="TransformExecutionPhase.FhirResource"/> — rules that run against a whole FHIR resource for
/// a FHIR-native destination, which has no mapped columns for a PostMapping rule to attach to.
///
/// The tests that matter most here are the isolation ones: these rules share a table with the PostMapping rules
/// V1 resolves on every mapped row, so a missing phase filter anywhere would leak V2's rules into V1's pipeline
/// or into the shared Rules modal.
/// </summary>
public sealed class FhirResourceRulePhaseTests
{
    // A unique database name per test gives the isolation these tests need. Deliberately NOT paired with a
    // fresh InMemoryDatabaseRoot: each distinct root makes EF build another internal service provider, and the
    // suite already sits near EF's twenty-provider warning threshold — adding one more tips unrelated
    // in-memory tests over it, which surfaces as ManyServiceProvidersCreatedWarning failures far from here.
    private readonly string _databaseName = Guid.NewGuid().ToString();

    private FHIRBridgeDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<FHIRBridgeDbContext>()
            .UseInMemoryDatabase(_databaseName)
            .Options);

    /// <summary>CreatedBy is required and normally stamped by the auditing interceptor, which doesn't run
    /// against a bare in-memory context — so every rule these tests persist is stamped by hand.</summary>
    private static TransformationRule Persisted(TransformationRule rule)
    {
        rule.MarkCreated("tests");
        return rule;
    }

    private static TransformationRule FhirRule(string sourceField, string resourceType = "Observation") =>
        Persisted(new TransformationRule(
            TransformScope.ResourceType,
            TransformNodeType.RoundingScaling,
            "{}",
            resourceType: resourceType,
            sourceField: sourceField,
            executionPhase: TransformExecutionPhase.FhirResource));

    private static TransformationRule PostMappingRule(string destinationField) =>
        Persisted(new TransformationRule(
            TransformScope.ResourceType,
            TransformNodeType.DateTimeFormat,
            "{}",
            resourceType: "Observation",
            destinationField: destinationField));

    [Fact]
    public void A_fhir_resource_rule_must_declare_the_path_it_reads()
    {
        // SourceField is a FHIR-resource rule's only key — with no destination column to attach to, a rule
        // without one would never fire and would give no clue why.
        var create = () => new TransformationRule(
            TransformScope.ResourceType,
            TransformNodeType.RoundingScaling,
            "{}",
            resourceType: "Observation",
            executionPhase: TransformExecutionPhase.FhirResource);

        create.Should().Throw<ArgumentException>().WithMessage("*SourceField*");
    }

    [Fact]
    public void A_fhir_resource_rule_cannot_carry_a_de_identification_profile()
    {
        // That pairing belongs to PreMapping. Allowing it would put one rule under two owners at once.
        var create = () => new TransformationRule(
            TransformScope.ResourceType,
            TransformNodeType.HashingMasking,
            "{}",
            resourceType: "Patient",
            sourceField: "name.family",
            executionPhase: TransformExecutionPhase.FhirResource,
            deIdentificationProfileId: Guid.NewGuid());

        create.Should().Throw<ArgumentException>();
    }

    // The isolation guarantee this used to check (PostMapping tier queries never return a FhirResource rule)
    // is now structural rather than something to test: WORKFLOW_V3_PLAN.md Step 3 removed every PostMapping
    // tier except GetWorkflowScopedAsync, which — like GetFhirResourceRulesAsync below — already filters on
    // its own ExecutionPhase explicitly, so there is no shared tier method left for a FhirResource rule to leak
    // through.

    [Fact]
    public async Task ListAsync_hides_fhir_resource_rules_but_still_shows_de_identification_rules()
    {
        // The Rules modal is shared by both builders and has always listed PostMapping AND PreMapping rows.
        // Excluding only the new phase keeps that screen byte-identical; filtering it to PostMapping instead
        // would have silently dropped de-identification rules from a list that shows them today.
        await using var context = CreateContext();
        context.TransformationRules.Add(FhirRule("valueQuantity.value"));
        context.TransformationRules.Add(PostMappingRule("EffectiveDate"));
        context.TransformationRules.Add(Persisted(new TransformationRule(
            TransformScope.ResourceType,
            TransformNodeType.HashingMasking,
            "{}",
            resourceType: "Observation",
            sourceField: "subject.reference",
            executionPhase: TransformExecutionPhase.PreMapping,
            deIdentificationProfileId: Guid.NewGuid())));
        await context.SaveChangesAsync();

        var repository = new EfTransformationRuleRepository(context);

        var listed = await repository.ListAsync(null, null, null, null, null, null, null, null, default);

        listed.Should().HaveCount(2);
        listed.Should().Contain(rule => rule.ExecutionPhase == TransformExecutionPhase.PostMapping);
        listed.Should().Contain(rule => rule.ExecutionPhase == TransformExecutionPhase.PreMapping);
        listed.Should().NotContain(rule => rule.ExecutionPhase == TransformExecutionPhase.FhirResource);

        var explicitlyRequested = await repository.ListAsync(
            null, null, null, null, null, null, null, TransformExecutionPhase.FhirResource, default);

        explicitlyRequested.Should().ContainSingle()
            .Which.ExecutionPhase.Should().Be(TransformExecutionPhase.FhirResource);
    }

    [Fact]
    public async Task GetFhirResourceRulesAsync_returns_only_the_requested_tier_and_resource_type()
    {
        await using var context = CreateContext();
        context.TransformationRules.Add(FhirRule("valueQuantity.value"));
        context.TransformationRules.Add(FhirRule("name.family", resourceType: "Patient"));
        context.TransformationRules.Add(PostMappingRule("EffectiveDate"));
        await context.SaveChangesAsync();

        var repository = new EfTransformationRuleRepository(context);

        var observation = await repository.GetFhirResourceRulesAsync(
            TransformScope.ResourceType, "Observation", null, null, null, null, default);

        observation.Should().ContainSingle()
            .Which.SourceField.Should().Be("valueQuantity.value");

        // A tier with no rows returns empty rather than falling back — the caller walks the tiers itself.
        var workflowTier = await repository.GetFhirResourceRulesAsync(
            TransformScope.Workflow, "Observation", null, null, null, null, default);

        workflowTier.Should().BeEmpty();
    }

    [Fact]
    public async Task GetFhirResourceRulesAsync_narrows_to_the_requested_source_path()
    {
        // Choosing between a path-specific and a blanket rule is the RESOLVER's job (next slice); the
        // repository's contract is only that a rule for a different path is never a candidate.
        await using var context = CreateContext();
        context.TransformationRules.Add(FhirRule("valueQuantity.value"));
        context.TransformationRules.Add(FhirRule("status"));
        await context.SaveChangesAsync();

        var repository = new EfTransformationRuleRepository(context);

        var rules = await repository.GetFhirResourceRulesAsync(
            TransformScope.ResourceType, "Observation", "valueQuantity.value", null, null, null, default);

        rules.Should().ContainSingle()
            .Which.SourceField.Should().Be("valueQuantity.value");
    }
}
