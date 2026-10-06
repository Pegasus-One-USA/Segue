using FHIRBridge.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations;

public sealed class EhrWriteLedgerEntryConfiguration : IEntityTypeConfiguration<EhrWriteLedgerEntry>
{
    /// <summary>Explicit and short: <see cref="EfEhrWriteLedgerRepository"/> recognises a lost insert race by this
    /// name on both providers, and PostgreSQL truncates generated names past 63 characters.</summary>
    public const string IdempotencyIndexName = "IX_EhrWriteLedger_Idempotency";

    public void Configure(EntityTypeBuilder<EhrWriteLedgerEntry> builder)
    {
        builder.ToTable("EhrWriteLedgerEntries");
        builder.HasKey(x => x.Id);

        // SHA-256 as uppercase hex is 64 characters.
        builder.Property(x => x.TargetKey).HasMaxLength(64).IsRequired();
        builder.Property(x => x.SourceKey).HasMaxLength(64).IsRequired();
        builder.Property(x => x.ContentHash).HasMaxLength(64).IsRequired();
        builder.Property(x => x.ResourceType).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Operation).HasConversion<string>().HasMaxLength(20).IsRequired();
        // Also a concurrency token: a reviewer resolving a row and a run claiming it change State without both
        // touching AttemptCount, so whichever saves second matches zero rows and fails instead of overwriting.
        builder.Property(x => x.State).HasMaxLength(20).IsRequired().IsConcurrencyToken();
        builder.Property(x => x.TargetResourceId).HasMaxLength(200);
        builder.Property(x => x.OutcomeCodes).HasMaxLength(200);
        builder.Property(x => x.ReviewedBy).HasMaxLength(256);
        builder.Property(x => x.TargetConnectionId).IsRequired();
        // Optimistic concurrency for claims: every send increments it, so a second run's claim on the same row
        // matches zero rows and fails instead of sending a duplicate. A plain column works the same on both
        // providers (a RowVersion would not: PostgreSQL has no rowversion type).
        builder.Property(x => x.AttemptCount).IsConcurrencyToken();
        builder.Property(x => x.CreatedOnUtc).IsRequired();
        builder.Property(x => x.UpdatedOnUtc).IsRequired();

        builder.HasIndex(x => new { x.TargetKey, x.ResourceType, x.SourceKey })
            .IsUnique()
            .HasDatabaseName(IdempotencyIndexName);

        // The review list: rows awaiting a person, newest first.
        builder.HasIndex(x => new { x.State, x.UpdatedOnUtc })
            .HasDatabaseName("IX_EhrWriteLedger_State_UpdatedOnUtc");
        builder.HasIndex(x => x.WorkflowRunId)
            .HasDatabaseName("IX_EhrWriteLedger_WorkflowRunId");
    }
}
