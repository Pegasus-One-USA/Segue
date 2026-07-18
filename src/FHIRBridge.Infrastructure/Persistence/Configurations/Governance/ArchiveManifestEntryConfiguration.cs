using FHIRBridge.Domain.Entities.Governance;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations.Governance;

public sealed class ArchiveManifestEntryConfiguration : IEntityTypeConfiguration<ArchiveManifestEntry>
{
    public void Configure(EntityTypeBuilder<ArchiveManifestEntry> builder)
    {
        builder.ToTable("ArchiveManifestEntries");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.DataClass).HasMaxLength(100).IsRequired();
        builder.Property(x => x.FileLocation).HasMaxLength(1000).IsRequired();

        builder.HasIndex(x => x.DataClass);
        builder.HasIndex(x => x.CreatedOnUtc);
    }
}
