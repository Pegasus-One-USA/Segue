using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace FHIRBridge.UnitTests.Infrastructure;

/// <summary>
/// Permission revocations must be physical deletes (a soft-deleted row keeps occupying the unique index), and used
/// to be done with <c>ExecuteDeleteAsync</c>, which bypassed <see cref="AuditingSaveChangesInterceptor"/> and left no
/// trace. These pin that every revocation now leaves a "Deleted" audit row, stays physical, and that saving a role's
/// or user's permissions records only what actually changed.
/// </summary>
public sealed class PermissionRevocationAuditTests
{
    private const string Allocation = nameof(PermissionAllocation);

    private readonly string _databaseName = SharedInMemoryDatabase.NewDatabaseName();
    private readonly Mock<ICurrentUserService> _currentUser = new();

    private readonly Guid _viewWorkflows = Guid.NewGuid();
    private readonly Guid _runWorkflows = Guid.NewGuid();
    private readonly Guid _exportReports = Guid.NewGuid();

    public PermissionRevocationAuditTests()
    {
        _currentUser.SetupGet(x => x.CurrentUser)
            .Returns(new CurrentUserInfo("admin", "admin@example.com", "Admin", ["Administrator"], true));

        using var context = CreateContext();
        context.Permissions.AddRange(
            new Permission(_viewWorkflows, "workflow.view", "View workflows", "View"),
            new Permission(_runWorkflows, "workflow.run", "Run workflows", "Run"),
            new Permission(_exportReports, "report.export", "Export reports", "Export"));
        context.SaveChanges();
    }

    private FHIRBridgeDbContext CreateContext() =>
        new(SharedInMemoryDatabase.Options<FHIRBridgeDbContext>(
            _databaseName, new AuditingSaveChangesInterceptor(_currentUser.Object)));

    private static EfUserAccessRepository CreateRepository(FHIRBridgeDbContext context) => new(context);

    private async Task<List<FHIRBridge.Domain.Entities.Governance.AuditLog>> AllocationAuditRowsAsync()
    {
        await using var context = CreateContext();
        return await context.AuditLogs.Where(x => x.EntityType == Allocation).ToListAsync();
    }

    private async Task<HashSet<Guid>> AllocationAuditRowIdsAsync() =>
        (await AllocationAuditRowsAsync()).Select(x => x.Id).ToHashSet();

    private async Task<List<FHIRBridge.Domain.Entities.Governance.AuditLog>> AllocationAuditRowsSinceAsync(
        HashSet<Guid> before) =>
        (await AllocationAuditRowsAsync()).Where(x => !before.Contains(x.Id)).ToList();

    [Fact]
    public async Task Adding_one_permission_to_a_role_writes_one_Created_row_and_leaves_the_rest_untouched()
    {
        var roleId = Guid.NewGuid();
        await using (var context = CreateContext())
        {
            await CreateRepository(context).SetRolePermissionsAsync(roleId, [_viewWorkflows, _runWorkflows], default);
        }

        Dictionary<Guid, Guid> idsBefore;
        await using (var context = CreateContext())
        {
            idsBefore = await context.PermissionAllocations.Where(x => x.RoleId == roleId)
                .ToDictionaryAsync(x => x.PermissionId, x => x.Id);
        }

        var rowsBefore = await AllocationAuditRowIdsAsync();

        await using (var context = CreateContext())
        {
            await CreateRepository(context).SetRolePermissionsAsync(
                roleId, [_viewWorkflows, _runWorkflows, _exportReports], default);
        }

        var newRows = await AllocationAuditRowsSinceAsync(rowsBefore);
        newRows.Should().ContainSingle().Which.Action.Should().Be("Created");

        await using (var context = CreateContext())
        {
            var after = await context.PermissionAllocations.Where(x => x.RoleId == roleId).ToListAsync();
            after.Should().HaveCount(3);
            after.Single(x => x.PermissionId == _viewWorkflows).Id.Should().Be(idsBefore[_viewWorkflows]);
            after.Single(x => x.PermissionId == _runWorkflows).Id.Should().Be(idsBefore[_runWorkflows]);
        }
    }

    [Fact]
    public async Task Removing_a_permission_in_the_role_dialog_writes_a_Deleted_row_for_it()
    {
        var roleId = Guid.NewGuid();
        await using (var context = CreateContext())
        {
            await CreateRepository(context).SetRolePermissionsAsync(roleId, [_viewWorkflows, _runWorkflows], default);
        }

        await using (var context = CreateContext())
        {
            await CreateRepository(context).SetRolePermissionsAsync(roleId, [_viewWorkflows], default);
        }

        var deleted = (await AllocationAuditRowsAsync()).Where(x => x.Action == "Deleted").ToList();
        var row = deleted.Should().ContainSingle().Subject;
        row.OldValueJson.Should().Contain(_runWorkflows.ToString()).And.Contain(roleId.ToString());
        row.NewValueJson.Should().BeNull();
        row.Actor.Should().Be("admin@example.com");

        await using (var context = CreateContext())
        {
            (await context.PermissionAllocations.IgnoreQueryFilters()
                    .AnyAsync(x => x.RoleId == roleId && x.PermissionId == _runWorkflows))
                .Should().BeFalse("a revocation must stay a physical delete so the permission can be granted again");
        }
    }

    [Fact]
    public async Task Removing_a_single_role_permission_writes_a_Deleted_row()
    {
        var roleId = Guid.NewGuid();
        await using (var context = CreateContext())
        {
            await CreateRepository(context).SetRolePermissionsAsync(roleId, [_viewWorkflows, _runWorkflows], default);
        }

        await using (var context = CreateContext())
        {
            await CreateRepository(context).RemoveRolePermissionAsync(roleId, _runWorkflows, default);
        }

        (await AllocationAuditRowsAsync()).Where(x => x.Action == "Deleted")
            .Should().ContainSingle().Which.OldValueJson.Should().Contain(_runWorkflows.ToString());
    }

    [Fact]
    public async Task Saving_a_role_with_unchanged_permissions_writes_no_audit_row()
    {
        var roleId = Guid.NewGuid();
        await using (var context = CreateContext())
        {
            await CreateRepository(context).SetRolePermissionsAsync(roleId, [_viewWorkflows, _runWorkflows], default);
        }

        var rowsBefore = await AllocationAuditRowIdsAsync();

        await using (var context = CreateContext())
        {
            await CreateRepository(context).SetRolePermissionsAsync(roleId, [_runWorkflows, _viewWorkflows], default);
        }

        (await AllocationAuditRowsSinceAsync(rowsBefore)).Should().BeEmpty();
    }

    [Fact]
    public async Task Changing_a_users_direct_permissions_writes_one_row_per_actual_change()
    {
        var userId = Guid.NewGuid();
        await using (var context = CreateContext())
        {
            await CreateRepository(context).SetUserPermissionAllocationsAsync(
                userId, new Dictionary<Guid, bool> { [_viewWorkflows] = true, [_runWorkflows] = true }, default);
        }

        var rowsBefore = await AllocationAuditRowIdsAsync();

        await using (var context = CreateContext())
        {
            // view: grant -> deny (update); run: removed (delete); export: new deny (create).
            await CreateRepository(context).SetUserPermissionAllocationsAsync(
                userId, new Dictionary<Guid, bool> { [_viewWorkflows] = false, [_exportReports] = false }, default);
        }

        var newRows = await AllocationAuditRowsSinceAsync(rowsBefore);
        newRows.Select(x => x.Action).Should().BeEquivalentTo(["Updated", "Deleted", "Created"]);
        newRows.Single(x => x.Action == "Deleted").OldValueJson.Should().Contain(_runWorkflows.ToString());

        await using (var context = CreateContext())
        {
            var after = await context.PermissionAllocations.Where(x => x.UserId == userId).ToListAsync();
            after.Should().HaveCount(2);
            after.Single(x => x.PermissionId == _viewWorkflows).IsEnabled.Should().BeFalse();
        }
    }

    [Fact]
    public async Task Removing_a_users_direct_permission_writes_a_Deleted_row()
    {
        var userId = Guid.NewGuid();
        await using (var context = CreateContext())
        {
            await CreateRepository(context).AddUserPermissionAllocationAsync(userId, _exportReports, true, default);
        }

        await using (var context = CreateContext())
        {
            await CreateRepository(context).RemoveUserPermissionAllocationAsync(userId, _exportReports, default);
        }

        (await AllocationAuditRowsAsync()).Where(x => x.Action == "Deleted")
            .Should().ContainSingle().Which.OldValueJson.Should().Contain(userId.ToString());
    }

    [Fact]
    public async Task Deleting_a_user_audits_each_of_their_revoked_permissions()
    {
        var user = new User("ext-perm", "perm@example.com", "Perm User", Guid.NewGuid());
        await using (var context = CreateContext())
        {
            context.Users.Add(user);
            await context.SaveChangesAsync();
            await CreateRepository(context).SetUserPermissionAllocationsAsync(
                user.Id, new Dictionary<Guid, bool> { [_viewWorkflows] = true, [_runWorkflows] = false }, default);
        }

        await using (var context = CreateContext())
        {
            var tracked = await context.Users.SingleAsync(x => x.Id == user.Id);
            await CreateRepository(context).DeleteUserAsync(tracked, default);
        }

        (await AllocationAuditRowsAsync()).Count(x => x.Action == "Deleted").Should().Be(2);

        // One operation, one actor: the user's own soft delete is stamped with whoever deleted them, the same actor
        // as its audit row and the revoked permissions' rows (it used to say "system").
        await using (var context = CreateContext())
        {
            var deleted = await context.Users.IgnoreQueryFilters().SingleAsync(x => x.Id == user.Id);
            deleted.IsDeleted.Should().BeTrue();
            deleted.DeletedBy.Should().Be("admin@example.com");

            var userRow = await context.AuditLogs.SingleAsync(x => x.EntityType == nameof(User) && x.Action == "Deleted");
            userRow.Actor.Should().Be("admin@example.com");
        }

        await using (var context = CreateContext())
        {
            (await context.PermissionAllocations.IgnoreQueryFilters().AnyAsync(x => x.UserId == user.Id))
                .Should().BeFalse();
        }
    }

    [Fact]
    public async Task An_old_soft_deleted_row_is_cleared_without_an_audit_row_and_the_permission_can_be_granted_again()
    {
        // Rows soft-deleted before revocations became physical still sit on the unique index; a re-grant must
        // clear them first. They were already revoked back then, so clearing them is not a new revocation.
        var roleId = Guid.NewGuid();
        await using (var context = CreateContext())
        {
            var stale = PermissionAllocation.ForRole(Guid.NewGuid(), roleId, _viewWorkflows);
            stale.ApplyDeleted("someone", DateTime.UtcNow);
            context.PermissionAllocations.Add(stale);
            await context.SaveChangesAsync();
        }

        var rowsBefore = await AllocationAuditRowIdsAsync();

        await using (var context = CreateContext())
        {
            await CreateRepository(context).SetRolePermissionsAsync(roleId, [_viewWorkflows], default);
        }

        (await AllocationAuditRowsSinceAsync(rowsBefore))
            .Should().ContainSingle().Which.Action.Should().Be("Created");

        await using (var context = CreateContext())
        {
            var rows = await context.PermissionAllocations.IgnoreQueryFilters()
                .Where(x => x.RoleId == roleId && x.PermissionId == _viewWorkflows).ToListAsync();
            rows.Should().ContainSingle().Which.IsDeleted.Should().BeFalse();
        }
    }
}
