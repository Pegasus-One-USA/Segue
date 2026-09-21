using FHIRBridge.Domain.Entities.Licensing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations;

public sealed class LicenseHistoryEntryConfiguration : IEntityTypeConfiguration<LicenseHistoryEntry>
{
    public void Configure(EntityTypeBuilder<LicenseHistoryEntry> builder)
    {
        builder.ToTable("LicenseHistoryEntries");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Token).IsRequired();
        builder.Property(x => x.CustomerName).HasMaxLength(200);
        builder.Property(x => x.Edition).HasMaxLength(100);
        builder.Property(x => x.State).HasMaxLength(20).IsRequired();

        builder.HasIndex(x => x.AppliedUtc);
    }
}
