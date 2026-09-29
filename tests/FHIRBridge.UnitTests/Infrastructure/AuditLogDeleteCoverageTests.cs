using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Persistence;
using FHIRBridge.Runtime.Domain.Workflows;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Moq;

namespace FHIRBridge.UnitTests.Infrastructure;

/// <summary>
/// Characterization tests for which deletes reach the AuditLogs trail.
///
/// The whole audit trail is produced by AuditingSaveChangesInterceptor — there is not one explicit
/// LogAuditAsync call site in the codebase — and its delete branch only fires for an entry that is both
/// IAuditableEntity and ISoftDeletable (it matches EntityState.Modified + IsDeleted, i.e. a Remove() the
/// same interceptor converted into a soft delete). An entity that is neither is deleted with no audit row
/// at all, silently.
/// </summary>
public sealed class AuditLogDeleteCoverageTests
{
    // See DestinationConfigurationSoftDeleteTests for why this root is shared and static.
    private static readonly InMemoryDatabaseRoot _root = new();
    private readonly string _databaseName = Guid.NewGuid().ToString();

    private FHIRBridgeDbContext CreateContext()
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(x => x.CurrentUser)
            .Returns(new CurrentUserInfo("admin", "admin@example.com", "Admin", ["Administrator"], true));

        var options = new DbContextOptionsBuilder<FHIRBridgeDbContext>()
            .UseInMemoryDatabase(_databaseName, _root)
            .AddInterceptors(new AuditingSaveChangesInterceptor(currentUser.Object))
            .Options;

        return new FHIRBridgeDbContext(options);
    }

    /// <summary>The working baseline: a soft-deletable config entity DOES produce a "Deleted" audit row.</summary>
    [Fact]
    public async Task Deleting_a_soft_deletable_config_entity_writes_an_audit_row()
    {
        var destination = new DestinationConfiguration(
            "Warehouse", DestinationType.SqlServer, new SecretReference("kv-1", "secret-1"), "dbo.Patients");

        await using (var context = CreateContext())
        {
            context.DestinationConfigurations.Add(destination);
            await context.SaveChangesAsync();
        }

        await using (var context = CreateContext())
        {
            var loaded = await context.DestinationConfigurations.FirstAsync(x => x.Id == destination.Id);
            context.DestinationConfigurations.Remove(loaded);
            await context.SaveChangesAsync();
        }

        await using (var context = CreateContext())
        {
            var deleteRows = await context.AuditLogs
                .Where(x => x.EntityId == destination.Id.ToString() && x.Action == "Deleted")
                .ToListAsync();

            deleteRows.Should().HaveCount(1, "a soft-deletable IAuditableEntity is covered by the interceptor");
            deleteRows[0].EntityType.Should().Be(nameof(DestinationConfiguration));
            deleteRows[0].Actor.Should().Be("admin@example.com", "CurrentUserInfo.AuditName prefers the email");
        }
    }

    /// <summary>
    /// WorkflowDefinition is a Runtime-domain aggregate implementing neither IAuditableEntity nor ISoftDeletable,
    /// so the interceptor still writes nothing for it — by design. Its audit trail is produced explicitly by
    /// SqlWorkflowDefinitionStore instead (see SqlWorkflowDefinitionStoreAuditTests), because every save there is
    /// a delete-and-re-add that EF reports as "Added", which the generic mechanism would log as "Created" on
    /// every edit.
    /// <para>This test therefore pins the INTERCEPTOR's behaviour, not the feature's: it guards against someone
    /// "fixing" the gap by adding IAuditableEntity to WorkflowDefinition, which would silently produce a wrong
    /// trail (every edit a Create, plus a spurious Delete) on top of the correct one the store writes.</para>
    /// </summary>
    [Fact]
    public async Task Deleting_a_workflow_writes_no_interceptor_audit_row_the_store_owns_that_trail()
    {
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "Nightly Epic Sync", version: 1);
        // WorkflowDefinition sits outside the interceptor's IAuditableEntity stamping, so nothing fills in its
        // EF-required CreatedBy for it — SqlWorkflowDefinitionStore.SaveAsync hand-rolls that via StampAudit.
        // Having to call it here is itself a symptom of the gap this test documents.
        workflow.StampAudit(DateTime.UtcNow, "admin@example.com", null, null);

        await using (var context = CreateContext())
        {
            context.WorkflowDefinitions.Add(workflow);
            await context.SaveChangesAsync();
        }

        await using (var context = CreateContext())
        {
            var loaded = await context.WorkflowDefinitions.FirstAsync(x => x.Id == workflow.Id);
            context.WorkflowDefinitions.Remove(loaded);
            await context.SaveChangesAsync();
        }

        await using (var context = CreateContext())
        {
            (await context.WorkflowDefinitions.AnyAsync(x => x.Id == workflow.Id))
                .Should().BeFalse("the delete is physical, not a soft delete");

            var anyAuditRow = await context.AuditLogs
                .Where(x => x.EntityId == workflow.Id.ToString())
                .ToListAsync();

            anyAuditRow.Should().BeEmpty(
                "the interceptor deliberately ignores WorkflowDefinition — SqlWorkflowDefinitionStore writes its "
                + "audit trail explicitly, because a save there is a delete-and-re-add EF reports as 'Added'");
        }
    }
}
