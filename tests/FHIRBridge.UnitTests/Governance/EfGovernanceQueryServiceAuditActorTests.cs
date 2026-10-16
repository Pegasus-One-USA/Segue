using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Entities.Governance;
using FHIRBridge.Infrastructure.Governance;
using FHIRBridge.Infrastructure.Persistence;
using FHIRBridge.UnitTests.Infrastructure;
using FluentAssertions;
using Moq;

namespace FHIRBridge.UnitTests.Governance;

/// <summary>
/// The audit trail stores the acting user's id; the Audit Logs screen must show (and sort by) the person's name.
/// </summary>
public sealed class EfGovernanceQueryServiceAuditActorTests
{
    private readonly string _databaseName = SharedInMemoryDatabase.NewDatabaseName();

    private FHIRBridgeDbContext CreateContext() =>
        new(SharedInMemoryDatabase.Options<FHIRBridgeDbContext>(_databaseName));

    private static EfGovernanceQueryService CreateSut(FHIRBridgeDbContext context) =>
        new(context, Mock.Of<IConfiguredPipelineRunRepository>(), Enumerable.Empty<IPurgeableStore>(), Mock.Of<IRetentionPolicyService>());

    private static AuditLog Row(string actor, string correlationId) =>
        Row(actor, correlationId, "Role", "Updated", DateTime.UtcNow);

    private static AuditLog Row(string actor, string correlationId, string module, string action, DateTime occurredOnUtc) =>
        new(Guid.NewGuid(), occurredOnUtc, actor, module, action, module, Guid.NewGuid().ToString(), "Audit",
            null, null, "Success", null, null, null, correlationId, null);

    private async Task<(User Zara, User Bob, User Deleted)> SeedAsync(string correlationId)
    {
        var zara = new User("ext-z", "zara@example.com", "zara khan", Guid.NewGuid());
        var bob = new User("ext-b", "bob@example.com", "Bob Smith", Guid.NewGuid());
        var deleted = new User("ext-d", "gone@example.com", "Deleted Person", Guid.NewGuid());
        deleted.ApplyDeleted("admin", DateTime.UtcNow);
        foreach (var user in new[] { zara, bob, deleted })
        {
            user.ApplyCreated("seed", DateTime.UtcNow);
        }

        await using var context = CreateContext();
        context.Users.AddRange(zara, bob, deleted);
        context.AuditLogs.AddRange(
            Row(zara.Id.ToString(), correlationId),
            Row(bob.Id.ToString(), correlationId),
            Row(deleted.Id.ToString(), correlationId),
            Row("anonymous", correlationId),
            Row("legacy@example.com", correlationId));
        await context.SaveChangesAsync();
        return (zara, bob, deleted);
    }

    [Fact]
    public async Task The_actor_is_shown_as_the_users_name_not_their_id()
    {
        var correlationId = Guid.NewGuid().ToString();
        var (zara, bob, deleted) = await SeedAsync(correlationId);

        await using var context = CreateContext();
        var page = await CreateSut(context).GetAuditLogsAsync(correlationId, null, null, 0, 50, default);

        var actors = page.Items.Select(x => x.Actor).ToList();
        actors.Should().BeEquivalentTo(["zara khan", "Bob Smith", "Deleted Person", "anonymous", "legacy@example.com"]);
        actors.Should().NotContain([zara.Id.ToString(), bob.Id.ToString(), deleted.Id.ToString()]);
    }

    [Fact]
    public async Task Sorting_by_actor_follows_the_name_case_insensitively()
    {
        var correlationId = Guid.NewGuid().ToString();
        await SeedAsync(correlationId);

        await using var context = CreateContext();
        var page = await CreateSut(context).GetAuditLogsAsync(
            correlationId, null, null, 0, 50, default, sortColumn: "actor", sortDirection: "asc");

        page.Items.Select(x => x.Actor).Should().Equal(
            "anonymous", "Bob Smith", "Deleted Person", "legacy@example.com", "zara khan");
    }

    [Fact]
    public async Task A_user_without_a_display_name_is_shown_by_first_and_last_name()
    {
        var correlationId = Guid.NewGuid().ToString();
        var user = new User("ext-n", "noname@example.com", null, Guid.NewGuid());
        user.UpdateName("Asha", "Rao");
        user.ApplyCreated("seed", DateTime.UtcNow);

        await using (var context = CreateContext())
        {
            context.Users.Add(user);
            context.AuditLogs.Add(Row(user.Id.ToString(), correlationId));
            await context.SaveChangesAsync();
        }

        await using (var context = CreateContext())
        {
            var page = await CreateSut(context).GetAuditLogsAsync(correlationId, null, null, 0, 50, default);
            page.Items.Single().Actor.Should().Be("Asha Rao");
        }
    }

    [Fact]
    public async Task Filters_narrow_the_list_by_module_actor_action_and_date()
    {
        var correlationId = Guid.NewGuid().ToString();
        var user = new User("ext-f", "filter@example.com", "Filter User", Guid.NewGuid());
        user.ApplyCreated("seed", DateTime.UtcNow);
        var day = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);

        await using (var context = CreateContext())
        {
            context.Users.Add(user);
            context.AuditLogs.AddRange(
                Row(user.Id.ToString(), correlationId, "Role", "Updated", day),
                Row(user.Id.ToString(), correlationId, "Role", "Deleted", day.AddDays(-3)),
                Row(user.Id.ToString(), correlationId, "SourceConnection", "Created", day),
                Row("anonymous", correlationId, "Role", "Updated", day));
            await context.SaveChangesAsync();
        }

        async Task<IReadOnlyList<string>> ActionsFor(AuditLogFilter filter)
        {
            await using var context = CreateContext();
            var page = await CreateSut(context).GetAuditLogsAsync(correlationId, null, null, 0, 50, default, filter: filter);
            return page.Items.Select(x => $"{x.Module}/{x.Action}/{x.Actor}").ToList();
        }

        (await ActionsFor(new AuditLogFilter(Module: "SourceConnection")))
            .Should().Equal("SourceConnection/Created/Filter User");
        (await ActionsFor(new AuditLogFilter(Actor: "anonymous")))
            .Should().Equal("Role/Updated/anonymous");
        (await ActionsFor(new AuditLogFilter(Action: "Deleted")))
            .Should().Equal("Role/Deleted/Filter User");
        (await ActionsFor(new AuditLogFilter(Module: "Role", Actor: user.Id.ToString())))
            .Should().BeEquivalentTo(["Role/Updated/Filter User", "Role/Deleted/Filter User"]);

        // From inclusive, To exclusive: the row three days earlier is left out.
        (await ActionsFor(new AuditLogFilter(FromUtc: day.Date, ToUtc: day.Date.AddDays(1))))
            .Should().HaveCount(3).And.NotContain("Role/Deleted/Filter User");

        // A date sent without an offset is read as UTC.
        (await ActionsFor(new AuditLogFilter(FromUtc: DateTime.SpecifyKind(day.Date.AddDays(-5), DateTimeKind.Unspecified),
                ToUtc: DateTime.SpecifyKind(day.Date.AddDays(-2), DateTimeKind.Unspecified))))
            .Should().Equal("Role/Deleted/Filter User");
    }

    [Fact]
    public async Task Filter_options_list_each_module_and_actor_once_with_actor_names()
    {
        var correlationId = Guid.NewGuid().ToString();
        var (zara, bob, deleted) = await SeedAsync(correlationId);

        await using var context = CreateContext();
        var options = await CreateSut(context).GetAuditLogFilterOptionsAsync(default);

        options.Modules.Should().Contain("Role").And.OnlyHaveUniqueItems();
        options.Actors.Select(x => x.Value).Should().OnlyHaveUniqueItems();
        options.Actors.Should().Contain(new AuditLogActorOptionDto(zara.Id.ToString(), "zara khan"));
        options.Actors.Should().Contain(new AuditLogActorOptionDto(deleted.Id.ToString(), "Deleted Person"));
        options.Actors.Should().Contain(new AuditLogActorOptionDto("anonymous", "anonymous"));
        options.Actors.Select(x => x.Name).Should().BeInAscendingOrder(StringComparer.OrdinalIgnoreCase);
        options.Actors.Select(x => x.Value).Should().Contain(bob.Id.ToString());
    }

    [Fact]
    public async Task A_user_with_blank_names_is_shown_by_email_in_the_list_and_the_filter_alike()
    {
        var correlationId = Guid.NewGuid().ToString();
        var user = new User("ext-blank", "blank@example.com", "  ", Guid.NewGuid());
        user.UpdateName("", " ");
        user.ApplyCreated("seed", DateTime.UtcNow);

        await using (var context = CreateContext())
        {
            context.Users.Add(user);
            context.AuditLogs.Add(Row(user.Id.ToString(), correlationId));
            await context.SaveChangesAsync();
        }

        await using (var context = CreateContext())
        {
            var sut = CreateSut(context);
            var listed = await sut.GetAuditLogsAsync(correlationId, null, null, 0, 50, default);
            var sorted = await sut.GetAuditLogsAsync(correlationId, null, null, 0, 50, default, "actor", "asc");
            var options = await sut.GetAuditLogFilterOptionsAsync(default);

            listed.Items.Single().Actor.Should().Be("blank@example.com");
            sorted.Items.Single().Actor.Should().Be("blank@example.com");
            options.Actors.Should().Contain(new AuditLogActorOptionDto(user.Id.ToString(), "blank@example.com"));
        }
    }

    [Fact]
    public async Task Rows_stored_under_a_users_legacy_email_count_as_that_user()
    {
        // Rows written before actors were stored as ids carry the e-mail; the same person must appear once in the
        // Actor filter, be named the same way, and filtering by them must return both kinds of row.
        var correlationId = Guid.NewGuid().ToString();
        var user = new User("ext-legacy", "legacy-user@example.com", "Legacy User", Guid.NewGuid());
        user.ApplyCreated("seed", DateTime.UtcNow);

        await using (var context = CreateContext())
        {
            context.Users.Add(user);
            context.AuditLogs.AddRange(
                Row(user.Id.ToString(), correlationId, "Role", "Updated", DateTime.UtcNow),
                Row("legacy-user@example.com", correlationId, "Role", "Created", DateTime.UtcNow.AddDays(-400)));
            await context.SaveChangesAsync();
        }

        await using (var context = CreateContext())
        {
            var sut = CreateSut(context);

            var options = await sut.GetAuditLogFilterOptionsAsync(default);
            options.Actors.Where(x => x.Name == "Legacy User").Should().ContainSingle()
                .Which.Value.Should().Be(user.Id.ToString());

            var filtered = await sut.GetAuditLogsAsync(
                correlationId, null, null, 0, 50, default, filter: new AuditLogFilter(Actor: user.Id.ToString()));
            filtered.Items.Select(x => x.Action).Should().BeEquivalentTo(["Updated", "Created"]);
            filtered.Items.Select(x => x.Actor).Should().AllBe("Legacy User");
        }
    }

    [Fact]
    public async Task A_legacy_email_in_a_different_case_still_resolves_and_filters_as_that_user()
    {
        var correlationId = Guid.NewGuid().ToString();
        var user = new User("ext-case", "case.user@example.com", "Case User", Guid.NewGuid());
        user.ApplyCreated("seed", DateTime.UtcNow);

        await using (var context = CreateContext())
        {
            context.Users.Add(user);
            context.AuditLogs.AddRange(
                Row(user.Id.ToString(), correlationId),
                Row("Case.User@Example.COM", correlationId));
            await context.SaveChangesAsync();
        }

        await using (var context = CreateContext())
        {
            var filtered = await CreateSut(context).GetAuditLogsAsync(
                correlationId, null, null, 0, 50, default, filter: new AuditLogFilter(Actor: user.Id.ToString()));

            filtered.Items.Should().HaveCount(2);
            filtered.Items.Select(x => x.Actor).Should().AllBe("Case User");
        }
    }

    [Fact]
    public async Task When_two_users_share_an_email_the_list_and_the_actor_sort_pick_the_same_one()
    {
        // E-mail is not unique (a deleted user's address can be reused). Both paths prefer the active user.
        var correlationId = Guid.NewGuid().ToString();
        var former = new User("ext-former", "shared@example.com", "Former Owner", Guid.NewGuid());
        former.ApplyDeleted("admin", DateTime.UtcNow);
        var current = new User("ext-current", "shared@example.com", "Current Owner", Guid.NewGuid());
        foreach (var u in new[] { former, current })
        {
            u.ApplyCreated("seed", DateTime.UtcNow);
        }

        await using (var context = CreateContext())
        {
            context.Users.AddRange(former, current);
            context.AuditLogs.Add(Row("shared@example.com", correlationId));
            await context.SaveChangesAsync();
        }

        await using (var context = CreateContext())
        {
            var sut = CreateSut(context);
            var listed = await sut.GetAuditLogsAsync(correlationId, null, null, 0, 50, default);
            var sorted = await sut.GetAuditLogsAsync(correlationId, null, null, 0, 50, default, "actor", "asc");

            listed.Items.Single().Actor.Should().Be("Current Owner");
            sorted.Items.Single().Actor.Should().Be("Current Owner");
        }
    }
}
