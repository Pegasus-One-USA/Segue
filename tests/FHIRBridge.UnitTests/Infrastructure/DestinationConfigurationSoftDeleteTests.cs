using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Persistence;
using FHIRBridge.SharedKernel.Enums;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Moq;

namespace FHIRBridge.UnitTests.Infrastructure;

/// <summary>
/// Regression test for a bug in AuditingSaveChangesInterceptor surfaced by the new destination-delete endpoint:
/// deleting a DestinationConfiguration is converted to a soft delete by flipping its EntityState from Deleted to
/// Modified, but its owned SecretReference (KeyVaultName/SecretName — inline columns on the same table) is
/// tracked as a separate entry that stays Deleted, which made EF write NULLs into those columns instead of
/// preserving them (SQL Server rejected it outright: "Cannot insert the value NULL into column 'KeyVaultName'").
/// No entity with an owned type had ever been soft-deleted before this endpoint existed, so the bug was latent.
/// </summary>
public sealed class DestinationConfigurationSoftDeleteTests
{
    private readonly InMemoryDatabaseRoot _root = new();
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

    [Fact]
    public async Task Soft_deleting_a_destination_preserves_its_owned_SecretReference()
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
            var reloaded = await context.DestinationConfigurations
                .IgnoreQueryFilters()
                .FirstAsync(x => x.Id == destination.Id);

            reloaded.IsDeleted.Should().BeTrue();
            reloaded.SecretReference.KeyVaultName.Should().Be("kv-1");
            reloaded.SecretReference.SecretName.Should().Be("secret-1");
        }
    }
}
