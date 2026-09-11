using FHIRBridge.Domain.Entities.Licensing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations.Licensing;

public sealed class UsageLedgerEntryConfiguration : IEntityTypeConfiguration<UsageLedgerEntry>
{
    public void Configure(EntityTypeBuilder<UsageLedgerEntry> builder)
    {
        builder.ToTable("UsageLedgerEntries");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.SequenceNumber).ValueGeneratedOnAdd().UseIdentityColumn();
        builder.Property(x => x.PreviousHash).HasMaxLength(128);
        builder.Property(x => x.EntryHash).HasMaxLength(128).IsRequired();

        builder.HasIndex(x => x.SequenceNumber).IsUnique();
        builder.HasIndex(x => x.ObservedUtc);
    }
}
