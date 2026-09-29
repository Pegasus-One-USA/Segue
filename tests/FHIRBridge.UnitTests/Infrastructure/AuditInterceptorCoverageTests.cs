using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Moq;

namespace FHIRBridge.UnitTests.Infrastructure;

/// <summary>
/// Covers the entities brought into the audit trail alongside the workflow work, and the two interceptor changes
/// they required: resolving an entity's key from its real primary key (not a property named "Id"), and auditing a
/// physical delete for an entity that is auditable but deliberately not soft-deletable.
/// </summary>
public sealed class AuditInterceptorCoverageTests
{
    private readonly string _databaseName = SharedInMemoryDatabase.NewDatabaseName();

    private FHIRBridgeDbContext CreateContext()
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(x => x.CurrentUser)
            .Returns(new CurrentUserInfo("admin", "admin@example.com", "Admin", ["Administrator"], true));

        return new FHIRBridgeDbContext(
            SharedInMemoryDatabase.Options<FHIRBridgeDbContext>(
                _databaseName, new AuditingSaveChangesInterceptor(currentUser.Object)));
    }

    [Fact]
    public async Task Granting_a_role_writes_a_Created_row_keyed_on_the_composite_key()
    {
        // UserRole has no "Id" property at all. Before the key fix this threw inside SavingChangesAsync, failing
        // the whole save rather than just the audit row.
        var userId = Guid.NewGuid();
        var roleId = Guid.NewGuid();

        await using (var context = CreateContext())
        {
            context.UserRoles.Add(new UserRole(userId, roleId));
            await context.SaveChangesAsync();
        }

        await using (var context = CreateContext())
        {
            var row = await context.AuditLogs
                .SingleAsync(x => x.EntityType == nameof(UserRole) && x.Action == "Created");

            row.EntityId.Should().Be($"{userId}|{roleId}");
            row.Actor.Should().Be("admin@example.com");
        }
    }

    [Fact]
    public async Task Revoking_a_role_writes_a_Deleted_row_that_still_names_who_held_it()
    {
        // The revocation is a PHYSICAL delete (UserRole is deliberately not ISoftDeletable so a revoked role
        // genuinely stops existing), so without the interceptor's EntityState.Deleted branch this left no trace.
        var userId = Guid.NewGuid();
        var roleId = Guid.NewGuid();

        await using (var context = CreateContext())
        {
            context.UserRoles.Add(new UserRole(userId, roleId));
            await context.SaveChangesAsync();
        }

        await using (var context = CreateContext())
        {
            var link = await context.UserRoles.FirstAsync(x => x.UserId == userId && x.RoleId == roleId);
            context.UserRoles.Remove(link);
            await context.SaveChangesAsync();
        }

        await using (var context = CreateContext())
        {
            (await context.UserRoles.AnyAsync(x => x.UserId == userId && x.RoleId == roleId))
                .Should().BeFalse("a revoked role must genuinely stop existing, not linger as a filtered row");

            var row = await context.AuditLogs
                .SingleAsync(x => x.EntityType == nameof(UserRole) && x.Action == "Deleted");

            row.EntityId.Should().Be($"{userId}|{roleId}");
            row.OldValueJson.Should().Contain(roleId.ToString(), "the removed assignment is the only record left");
        }
    }

    [Fact]
    public async Task A_provisioned_secrets_value_is_redacted_from_the_audit_row()
    {
        // AuditLogs is append-only and retained for years. Recording that a secret was provisioned is the point;
        // copying the material into a permanent table is not.
        const string ProtectedValue = "CfDJ8-super-secret-protected-payload";

        await using (var context = CreateContext())
        {
            context.ProvisionedSecrets.Add(new ProvisionedSecret("kv-1", "epic-client-secret", ProtectedValue));
            await context.SaveChangesAsync();
        }

        await using (var context = CreateContext())
        {
            var row = await context.AuditLogs
                .SingleAsync(x => x.EntityType == nameof(ProvisionedSecret) && x.Action == "Created");

            row.NewValueJson.Should().NotContain(ProtectedValue);
            row.NewValueJson.Should().Contain("***redacted***");
            row.NewValueJson.Should().Contain("epic-client-secret", "which secret it was still has to be visible");
        }
    }

    [Fact]
    public async Task A_single_key_entity_still_reports_a_bare_id()
    {
        // Guards the key-resolution change against altering existing rows' shape: anything with a plain Id must
        // keep producing exactly what Property("Id") did, or the (EntityType, EntityId) index stops matching.
        var binding = new UserFhirContextBinding(
            Guid.NewGuid(), "user@example.com", FhirContextResourceType.Patient, "patient-123");

        await using (var context = CreateContext())
        {
            context.UserFhirContextBindings.Add(binding);
            await context.SaveChangesAsync();
        }

        await using (var context = CreateContext())
        {
            var row = await context.AuditLogs
                .SingleAsync(x => x.EntityType == nameof(UserFhirContextBinding) && x.Action == "Created");

            row.EntityId.Should().Be(binding.Id.ToString());
            row.EntityId.Should().NotContain("|");
        }
    }
}
