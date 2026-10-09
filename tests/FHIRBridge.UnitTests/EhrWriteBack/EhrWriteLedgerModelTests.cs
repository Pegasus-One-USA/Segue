using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Infrastructure.Persistence;
using FHIRBridge.Infrastructure.Persistence.Configurations;
using FHIRBridge.UnitTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.UnitTests.EhrWriteBack;

/// <summary>
/// The ledger's duplicate guard is a unique index, which the EF InMemory provider does not enforce, so the index is
/// asserted on the model itself; the repository's race handling recognises a lost insert by this exact name.
/// </summary>
public sealed class EhrWriteLedgerModelTests
{
    [Fact]
    public void Idempotency_index_is_unique_on_target_resource_type_and_source()
    {
        using var context = new FHIRBridgeDbContext(SharedInMemoryDatabase.Options<FHIRBridgeDbContext>(SharedInMemoryDatabase.NewDatabaseName()));
        var entity = context.Model.FindEntityType(typeof(EhrWriteLedgerEntry))!;

        var index = entity.GetIndexes().Single(i => i.GetDatabaseName() == EhrWriteLedgerEntryConfiguration.IdempotencyIndexName);

        index.IsUnique.Should().BeTrue();
        index.Properties.Select(p => p.Name).Should().Equal(
            nameof(EhrWriteLedgerEntry.TargetKey), nameof(EhrWriteLedgerEntry.ResourceType), nameof(EhrWriteLedgerEntry.SourceKey));
        EhrWriteLedgerEntryConfiguration.IdempotencyIndexName.Length.Should().BeLessThan(63, because: "PostgreSQL truncates longer names");
    }

    [Fact]
    public void Ledger_row_is_neither_audited_nor_soft_deleted()
    {
        typeof(EhrWriteLedgerEntry).GetInterfaces().Select(i => i.Name)
            .Should().NotContain(["IAuditableEntity", "ISoftDeletable", "IAppendOnlyEntity"]);
    }

    [Fact]
    public void Source_connection_access_defaults_to_read()
    {
        using var context = new FHIRBridgeDbContext(SharedInMemoryDatabase.Options<FHIRBridgeDbContext>(SharedInMemoryDatabase.NewDatabaseName()));
        var property = context.Model.FindEntityType(typeof(SourceConnection))!.FindProperty(nameof(SourceConnection.Access))!;

        property.GetDefaultValue().Should().Be(SourceConnectionAccess.Read);
        property.IsNullable.Should().BeFalse();
    }

    [Theory]
    [InlineData(EhrWriteLedgerState.Written, true)]
    [InlineData(EhrWriteLedgerState.AlreadyAtTarget, true)]
    [InlineData(EhrWriteLedgerState.Unknown, true)]
    [InlineData(EhrWriteLedgerState.Pending, true)]
    [InlineData(EhrWriteLedgerState.Rejected, false)]
    public void Only_a_rejection_may_be_sent_again(string state, bool blocks)
    {
        EhrWriteLedgerState.BlocksResend(state).Should().Be(blocks);
    }
}
