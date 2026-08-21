using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Moq;

namespace FHIRBridge.UnitTests.Infrastructure;

/// <summary>
/// Regression test for a real duplicate-rows bug surfaced by the mapping-config import endpoint: re-importing
/// the same payload (which calls <see cref="MappingProfile.Update"/>, fully replacing the owned
/// <see cref="MappingProfile.Fields"/> collection) left the OLD <c>MappingFields</c> rows in place *and*
/// inserted new ones, instead of replacing them. Root cause: <see cref="AuditingSaveChangesInterceptor"/>
/// unconditionally flipped any Deleted owned entry to Unchanged/Modified — correct for a single-instance owned
/// type (e.g. SecretReference) cascading from its OWNER's soft delete, but wrong for an `OwnsMany` collection
/// item (its own independently-generated shadow key) removed during an entirely ordinary update: nothing about
/// that scenario means "owner cascade", so the old row was wrongly preserved instead of deleted. Uses the real
/// EF Core InMemory provider (not the hand-rolled <see cref="InMemoryConfigurationRepository"/> fake)
/// specifically so real change-tracking/interceptor behavior is exercised — the fake bypasses EF Core entirely
/// and can't catch this class of bug.
/// </summary>
public sealed class MappingProfileFieldReplacementTests
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

    private static MappingField Field(string targetField, string jsonPath) =>
        new(targetField, jsonPath, MappingValueType.String, IsRequired: false, DefaultValue: null, Format: "directField");

    [Fact]
    public async Task Updating_a_mapping_profile_replaces_its_fields_instead_of_duplicating_them()
    {
        Guid profileId;

        await using (var context = CreateContext())
        {
            var profile = new MappingProfile(
                "Patient", "Patient", Guid.NewGuid(), Guid.NewGuid(), "Patient",
                [Field("Id", "Patient.id"), Field("Active", "Patient.active")]);
            profileId = profile.Id;

            context.MappingProfiles.Add(profile);
            await context.SaveChangesAsync();
        }

        // Simulate a re-import: reload the profile and replace its Fields with a same-sized but different set —
        // exactly what MappingImportService does on every re-run.
        await using (var context = CreateContext())
        {
            var profile = await context.MappingProfiles.Include(x => x.Fields).FirstAsync(x => x.Id == profileId);
            profile.Update(
                "Patient", "Patient", profile.SourceConnectionId, profile.DestinationId, "Patient",
                [Field("Id", "Patient.id"), Field("Active", "Patient.active")]);
            await context.SaveChangesAsync();
        }

        await using (var context = CreateContext())
        {
            var reloaded = await context.MappingProfiles.Include(x => x.Fields).FirstAsync(x => x.Id == profileId);
            reloaded.Fields.Should().HaveCount(2, "the old field rows must be replaced, not left alongside new duplicates");
        }
    }
}
