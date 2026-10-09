using FHIRBridge.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations;

public sealed class TabularSourceFileConfiguration : IEntityTypeConfiguration<TabularSourceFile>
{
    public void Configure(EntityTypeBuilder<TabularSourceFile> builder)
    {
        builder.ToTable("TabularSourceFiles");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.FileName).HasMaxLength(260).IsRequired();
        // Ciphertext of up to 10 MB of CSV: an unbounded text column on both providers.
        builder.Property(x => x.EncryptedContent).IsRequired();
        builder.Property(x => x.ContentSha256).HasMaxLength(64).IsRequired();
        builder.Property(x => x.ColumnsJson).IsRequired();
        builder.Property(x => x.CreatedBy).HasMaxLength(256);
        builder.Property(x => x.CreatedOnUtc).IsRequired();

        builder.HasIndex(x => x.CreatedOnUtc).HasDatabaseName("IX_TabularSourceFiles_CreatedOnUtc");
    }
}
