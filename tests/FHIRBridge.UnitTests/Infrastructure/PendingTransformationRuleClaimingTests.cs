using FHIRBridge.Application.Services.Transforms;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace FHIRBridge.UnitTests.Infrastructure;

/// <summary>
/// Who may see and claim a PENDING workflow-scoped transformation rule — one the builder saved before its
/// workflow existed, so it carries Scope=Workflow with no ResourcePipelineRouteId and never runs until
/// something attaches it.
///
/// Pending rows have no workflow and no node to identify them, so before ownership narrowing the only filter
/// was the destination type. Two bugs followed, both reproduced before the fix and pinned here after it:
///   1. a workflow save claimed pending rules authored by anyone, including an abandoned session's;
///   2. deleting a destination node and re-creating the same column re-applied that node's old rule, because
///      the pending tier matches on destination type + resource type + destination field alone.
/// </summary>
public sealed class PendingTransformationRuleClaimingTests
{
    // Shared root, per-test database name — same reasoning as EfBulkExportJobRepositoryTests.
    private static readonly InMemoryDatabaseRoot _root = new();
    private readonly string _databaseName = Guid.NewGuid().ToString();

    private const string Author = "alice@example.com";
    private const string SomeoneElse = "bob@example.com";

    private FHIRBridgeDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<FHIRBridgeDbContext>()
            .UseInMemoryDatabase(_databaseName, _root)
            .Options;

        return new FHIRBridgeDbContext(options);
    }

    private static TransformationRule PendingRule(DestinationType destinationType, string destinationField) =>
        new(
            TransformScope.Workflow,
            TransformNodeType.StringNormalization,
            """{"case":"upper"}""",
            destinationType: destinationType,
            resourceType: "Patient",
            destinationField: destinationField,
            resourcePipelineRouteId: null);

    /// <param name="author">
    /// The provenance AuditingSaveChangesInterceptor would stamp in production. Stamped by hand because that
    /// interceptor is an Api/Worker host registration and is not attached to this bare test context.
    /// </param>
    private async Task SeedAsync(string? author, params TransformationRule[] rules)
    {
        await using var db = CreateContext();
        foreach (var rule in rules)
        {
            if (author is not null)
            {
                rule.MarkCreated(author);
            }

            db.TransformationRules.Add(rule);
        }

        await db.SaveChangesAsync();
    }

    // ── 1. claiming at workflow-save time ───────────────────────────────────────────────────────────

    [Fact]
    public async Task A_pending_rule_is_not_claimed_by_another_authors_workflow_save()
    {
        // Session A authored this and was abandoned. Session B saves an unrelated workflow that happens to
        // write to the same destination type. Before ownership narrowing this returned A's rule, and
        // AttachToWorkflow — which only refuses rules that ALREADY belong somewhere — let B claim it.
        var abandoned = PendingRule(DestinationType.PostgreSql, "Fullname");
        await SeedAsync(Author, abandoned);

        await using var db = CreateContext();
        var repository = new EfTransformationRuleRepository(db);

        var pending = await repository.GetPendingWorkflowRulesAsync(
            [DestinationType.PostgreSql], SomeoneElse, default);

        pending.Should().BeEmpty("a rule belongs to whoever authored it until it is attached");
    }

    [Fact]
    public async Task Its_own_author_still_claims_it_on_save()
    {
        // The case the pending tier exists for: rules authored before the workflow had an id must attach on
        // the save that gives it one. Narrowing must not break this.
        var mine = PendingRule(DestinationType.PostgreSql, "Fullname");
        await SeedAsync(Author, mine);

        await using var db = CreateContext();
        var repository = new EfTransformationRuleRepository(db);

        var pending = await repository.GetPendingWorkflowRulesAsync(
            [DestinationType.PostgreSql], Author, default);

        pending.Select(r => r.Id).Should().Contain(mine.Id);
    }

    [Fact]
    public async Task A_pending_rule_cannot_be_persisted_without_an_author()
    {
        // Why the ownership narrowing has no gap to fall through. CreatedBy is a REQUIRED column, so there
        // is no such thing as an unowned pending rule that would have to stay claimable by everyone — the
        // filter covers every row that can exist. If this ever starts passing, the narrowing has a hole and
        // abandoned rules become claimable again.
        var unowned = PendingRule(DestinationType.PostgreSql, "Fullname");

        var persist = async () => await SeedAsync(author: null, unowned);

        await persist.Should().ThrowAsync<DbUpdateException>()
            .WithMessage("*CreatedBy*");
    }

    [Fact]
    public async Task A_pending_rule_for_a_different_destination_type_is_left_alone()
    {
        var otherDestination = PendingRule(DestinationType.MySql, "Fullname");
        await SeedAsync(Author, otherDestination);

        await using var db = CreateContext();
        var repository = new EfTransformationRuleRepository(db);

        var pending = await repository.GetPendingWorkflowRulesAsync([DestinationType.PostgreSql], Author, default);

        pending.Should().BeEmpty();
    }

    [Fact]
    public async Task An_already_attached_rule_is_never_re_pointed()
    {
        var attached = PendingRule(DestinationType.PostgreSql, "Fullname");
        attached.AttachToWorkflow(Guid.NewGuid());
        await SeedAsync(Author, attached);

        await using var db = CreateContext();
        var repository = new EfTransformationRuleRepository(db);

        var pending = await repository.GetPendingWorkflowRulesAsync([DestinationType.PostgreSql], Author, default);

        pending.Should().BeEmpty("a non-null ResourcePipelineRouteId takes the row out of the pending set");
    }

    // ── 2. the reported scenario: delete the destination node, re-create the same mapping ───────────
    // Reported repro, through the real repository and resolver rather than a mock:
    //   1. new workflow, add an SQL node, map a column, add a transformation   (pending rule written)
    //   2. delete the SQL node without saving                                  (rules survive by design)
    //   3. add a new SQL node, re-create the same column, choose NO transformation
    //   4. before the fix, a transformation was applied anyway

    [Fact]
    public async Task Another_authors_orphan_no_longer_reaches_a_newly_created_mapping()
    {
        var orphaned = PendingRule(DestinationType.PostgreSql, "Fullname");
        await SeedAsync(Author, orphaned);

        await using var db = CreateContext();
        var resolver = new EffectiveRuleResolver(new EfTransformationRuleRepository(db));

        // The new node's mapping, workflow still unsaved (no route id) — the flags the field-mapping list and
        // the join popover actually send, both of which pass includePending: true.
        var effective = await resolver.ResolveAsync(
            DestinationType.PostgreSql, "Patient", "Fullname", null, null, null, null, default,
            workflowScopedOnly: true, includePendingWorkflowRules: true, pendingOwner: SomeoneElse);

        effective.Should().BeEmpty();
    }

    [Fact]
    public async Task The_author_still_sees_their_own_pending_rule_on_the_column_they_wrote_it_for()
    {
        // The legitimate case this must not break: author a rule, keep building, and the column keeps showing
        // it before the workflow has ever been saved.
        var mine = PendingRule(DestinationType.PostgreSql, "Fullname");
        await SeedAsync(Author, mine);

        await using var db = CreateContext();
        var resolver = new EffectiveRuleResolver(new EfTransformationRuleRepository(db));

        var effective = await resolver.ResolveAsync(
            DestinationType.PostgreSql, "Patient", "Fullname", null, null, null, null, default,
            workflowScopedOnly: true, includePendingWorkflowRules: true, pendingOwner: Author);

        effective.Select(r => r.Id).Should().Contain(mine.Id);
    }

    [Fact]
    public async Task A_caller_that_does_not_opt_into_pending_rules_never_sees_them()
    {
        var mine = PendingRule(DestinationType.PostgreSql, "Fullname");
        await SeedAsync(Author, mine);

        await using var db = CreateContext();
        var resolver = new EffectiveRuleResolver(new EfTransformationRuleRepository(db));

        var effective = await resolver.ResolveAsync(
            DestinationType.PostgreSql, "Patient", "Fullname", null, null, null, null, default,
            workflowScopedOnly: true, includePendingWorkflowRules: false, pendingOwner: Author);

        effective.Should().BeEmpty();
    }

    // ── 3. deleting a destination node cleans up its own drafts ────────────────────────────────────

    [Fact]
    public async Task Deleting_pending_rules_removes_only_the_callers_own()
    {
        var mine = PendingRule(DestinationType.PostgreSql, "Fullname");
        var theirs = PendingRule(DestinationType.PostgreSql, "Fullname");
        await SeedAsync(Author, mine);
        await SeedAsync(SomeoneElse, theirs);

        int deleted;
        await using (var db = CreateContext())
        {
            deleted = await new EfTransformationRuleRepository(db)
                .DeletePendingWorkflowRulesAsync([DestinationType.PostgreSql], Author, default);
        }

        deleted.Should().Be(1);

        await using var verify = CreateContext();
        var remaining = await verify.TransformationRules.Select(r => r.Id).ToListAsync();
        remaining.Should().NotContain(mine.Id).And.Contain(theirs.Id,
            "a canvas delete must clean up its own drafts without destroying another session's");
    }

    [Fact]
    public async Task Deleting_pending_rules_never_touches_an_attached_rule()
    {
        // An attached rule belongs to a saved workflow, not to the canvas being edited.
        var attached = PendingRule(DestinationType.PostgreSql, "Fullname");
        attached.AttachToWorkflow(Guid.NewGuid());
        await SeedAsync(Author, attached);

        await using (var db = CreateContext())
        {
            var deleted = await new EfTransformationRuleRepository(db)
                .DeletePendingWorkflowRulesAsync([DestinationType.PostgreSql], Author, default);
            deleted.Should().Be(0);
        }

        await using var verify = CreateContext();
        (await verify.TransformationRules.AnyAsync(r => r.Id == attached.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task Deleting_pending_rules_leaves_other_destination_types_alone()
    {
        var otherDestination = PendingRule(DestinationType.MySql, "Fullname");
        await SeedAsync(Author, otherDestination);

        await using (var db = CreateContext())
        {
            var deleted = await new EfTransformationRuleRepository(db)
                .DeletePendingWorkflowRulesAsync([DestinationType.PostgreSql], Author, default);
            deleted.Should().Be(0);
        }

        await using var verify = CreateContext();
        (await verify.TransformationRules.AnyAsync(r => r.Id == otherDestination.Id)).Should().BeTrue();
    }
}
