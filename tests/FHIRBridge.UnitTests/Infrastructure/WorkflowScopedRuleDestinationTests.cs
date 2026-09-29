using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace FHIRBridge.UnitTests.Infrastructure;

/// <summary>
/// Which destination an ATTACHED workflow-scoped rule belongs to.
///
/// The reported sequence: author a Concatenation rule on a PostgreSQL destination, save the rule and the
/// mapping, delete that destination, add ANOTHER PostgreSQL destination, map the same column — and the rule
/// was already applied to it. Nothing in the system could tell the two apart:
///   * the lookup matched on workflow + resource type + destination field;
///   * adding a destination TYPE equality does not help, because both are PostgreSql;
///   * and the save-time retirement pass drops a type from its "removed" set the moment any surviving
///     destination shares it, so replacing like for like retires nothing, ever.
///
/// The configuration id is the only thing that separates two destinations of the same kind, which is why the
/// rule now records it. A rule that recorded none is matched permissively, so rules written before the column
/// existed keep applying rather than silently stopping.
/// </summary>
public sealed class WorkflowScopedRuleDestinationTests
{
    private static readonly InMemoryDatabaseRoot _root = new();
    private readonly string _databaseName = Guid.NewGuid().ToString();

    private static readonly Guid Workflow = Guid.NewGuid();
    private static readonly Guid OldPostgres = Guid.NewGuid();
    private static readonly Guid NewPostgres = Guid.NewGuid();

    private FHIRBridgeDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<FHIRBridgeDbContext>()
            .UseInMemoryDatabase(_databaseName, _root)
            .Options;

        return new FHIRBridgeDbContext(options);
    }

    private async Task<TransformationRule> SeedAsync(
        DestinationType destinationType, Guid? destinationConfigurationId)
    {
        var rule = new TransformationRule(
            TransformScope.Workflow,
            TransformNodeType.ConcatenationTemplating,
            """{"mode":"concat","separator":"|"}""",
            destinationType: destinationType,
            resourceType: "Patient",
            destinationField: "Resource",
            resourcePipelineRouteId: Workflow,
            destinationConfigurationId: destinationConfigurationId);

        await using var db = CreateContext();
        rule.MarkCreated("alice@example.com");
        db.TransformationRules.Add(rule);
        await db.SaveChangesAsync();
        return rule;
    }

    private async Task<IReadOnlyList<TransformationRule>> QueryAsync(
        DestinationType? destinationType, Guid? destinationConfigurationId)
    {
        await using var db = CreateContext();
        return await new EfTransformationRuleRepository(db).GetWorkflowScopedAsync(
            Workflow, destinationType, destinationConfigurationId, "Patient", "Resource", null, null, default);
    }

    [Fact]
    public async Task A_replacement_destination_of_the_SAME_type_does_not_inherit_the_old_ones_rule()
    {
        // The exact reported case. Both are PostgreSQL, so every type-based check still matches — only the
        // configuration id separates them.
        var rule = await SeedAsync(DestinationType.PostgreSql, OldPostgres);

        var forTheNewDestination = await QueryAsync(DestinationType.PostgreSql, NewPostgres);

        forTheNewDestination.Select(r => r.Id).Should().NotContain(rule.Id,
            "a rule belongs to the destination it was authored against, not to every destination of that kind");
    }

    [Fact]
    public async Task The_destination_it_was_authored_against_still_gets_it()
    {
        // The half a narrowing fix can break: reopening the SAME destination must still show the rule.
        var rule = await SeedAsync(DestinationType.PostgreSql, OldPostgres);

        var forItsOwnDestination = await QueryAsync(DestinationType.PostgreSql, OldPostgres);

        forItsOwnDestination.Select(r => r.Id).Should().Contain(rule.Id);
    }

    [Fact]
    public async Task A_rule_that_recorded_no_destination_still_applies_anywhere()
    {
        // Every rule written before this column existed has a null id. Excluding those would stop them
        // running — a silent, widespread change — so they stay permissive. The cost is that an ALREADY
        // mis-attached rule does not fix itself; it has to be deleted once.
        var legacy = await SeedAsync(DestinationType.PostgreSql, destinationConfigurationId: null);

        var forTheNewDestination = await QueryAsync(DestinationType.PostgreSql, NewPostgres);

        forTheNewDestination.Select(r => r.Id).Should().Contain(legacy.Id);
    }

    [Fact]
    public async Task A_caller_that_does_not_know_the_destination_still_sees_every_rule()
    {
        // GetRuleImpactSummaryAsync counts how many routes already override a field, whatever they write to,
        // and the executors resolve with null so run-time behaviour is unchanged by this narrowing.
        var rule = await SeedAsync(DestinationType.PostgreSql, OldPostgres);

        var acrossAll = await QueryAsync(destinationType: null, destinationConfigurationId: null);

        acrossAll.Select(r => r.Id).Should().Contain(rule.Id);
    }

    [Fact]
    public async Task A_different_destination_TYPE_is_still_excluded()
    {
        // The coarser narrowing added earlier still holds, and covers rules that predate the id column.
        var rule = await SeedAsync(DestinationType.SqlServer, destinationConfigurationId: null);

        var forPostgres = await QueryAsync(DestinationType.PostgreSql, NewPostgres);

        forPostgres.Select(r => r.Id).Should().NotContain(rule.Id);
    }


    // ── deleting a destination takes its rules with it ──────────────────────────────────────────

    [Fact]
    public async Task Deleting_a_destination_removes_the_rules_it_owned()
    {
        var mine = await SeedAsync(DestinationType.PostgreSql, OldPostgres);

        int deleted;
        await using (var db = CreateContext())
        {
            deleted = await new EfTransformationRuleRepository(db).DeleteWorkflowRulesForDestinationAsync(
                Workflow, OldPostgres, DestinationType.PostgreSql, includeUnattributed: false, default);
        }

        deleted.Should().Be(1);
        await using var verify = CreateContext();
        (await verify.TransformationRules.AnyAsync(r => r.Id == mine.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task Deleting_a_destination_leaves_ANOTHER_destinations_rules_alone()
    {
        var other = await SeedAsync(DestinationType.PostgreSql, NewPostgres);

        await using (var db = CreateContext())
        {
            var deleted = await new EfTransformationRuleRepository(db).DeleteWorkflowRulesForDestinationAsync(
                Workflow, OldPostgres, DestinationType.PostgreSql, includeUnattributed: false, default);
            deleted.Should().Be(0);
        }

        await using var verify = CreateContext();
        (await verify.TransformationRules.AnyAsync(r => r.Id == other.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task Deleting_the_last_destination_of_a_type_also_clears_rules_that_recorded_none()
    {
        // The case that actually unblocks existing installations: every rule authored before the id column
        // existed has a null DestinationConfigurationId, so narrowing the LOOKUP alone left every one of
        // them still applying. With no destination of this type left, they can only have belonged to the
        // one being removed.
        var legacy = await SeedAsync(DestinationType.PostgreSql, destinationConfigurationId: null);

        await using (var db = CreateContext())
        {
            var deleted = await new EfTransformationRuleRepository(db).DeleteWorkflowRulesForDestinationAsync(
                Workflow, OldPostgres, DestinationType.PostgreSql, includeUnattributed: true, default);
            deleted.Should().Be(1);
        }

        await using var verify = CreateContext();
        (await verify.TransformationRules.AnyAsync(r => r.Id == legacy.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task A_rule_with_no_recorded_destination_is_kept_while_another_of_its_type_survives()
    {
        // It could belong to the survivor, and there is no way to tell — so it is left rather than guessed at.
        var legacy = await SeedAsync(DestinationType.PostgreSql, destinationConfigurationId: null);

        await using (var db = CreateContext())
        {
            var deleted = await new EfTransformationRuleRepository(db).DeleteWorkflowRulesForDestinationAsync(
                Workflow, OldPostgres, DestinationType.PostgreSql, includeUnattributed: false, default);
            deleted.Should().Be(0);
        }

        await using var verify = CreateContext();
        (await verify.TransformationRules.AnyAsync(r => r.Id == legacy.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task Deleting_a_destination_does_not_reach_into_another_workflow()
    {
        var rule = new TransformationRule(
            TransformScope.Workflow, TransformNodeType.ConcatenationTemplating, "{}",
            destinationType: DestinationType.PostgreSql, resourceType: "Patient", destinationField: "Resource",
            resourcePipelineRouteId: Guid.NewGuid(), destinationConfigurationId: OldPostgres);
        await using (var seed = CreateContext())
        {
            rule.MarkCreated("alice@example.com");
            seed.TransformationRules.Add(rule);
            await seed.SaveChangesAsync();
        }

        await using (var db = CreateContext())
        {
            var deleted = await new EfTransformationRuleRepository(db).DeleteWorkflowRulesForDestinationAsync(
                Workflow, OldPostgres, DestinationType.PostgreSql, includeUnattributed: true, default);
            deleted.Should().Be(0);
        }

        await using var verify = CreateContext();
        (await verify.TransformationRules.AnyAsync(r => r.Id == rule.Id)).Should().BeTrue();
    }
    [Fact]
    public async Task The_rule_row_records_the_destination_it_was_authored_against()
    {
        var rule = await SeedAsync(DestinationType.PostgreSql, OldPostgres);

        await using var db = CreateContext();
        var stored = await db.TransformationRules.SingleAsync(r => r.Id == rule.Id);

        stored.DestinationConfigurationId.Should().Be(OldPostgres);
    }
}
