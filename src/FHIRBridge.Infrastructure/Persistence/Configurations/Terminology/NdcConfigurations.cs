using FHIRBridge.Domain.Entities.Terminology;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations.Terminology;

public sealed class NdcProductConfiguration : IEntityTypeConfiguration<NdcProduct>
{
    public void Configure(EntityTypeBuilder<NdcProduct> builder)
    {
        builder.ToTable("NdcProducts", "terminology");
        builder.HasKey(x => x.ProductNdc);
        builder.Property(x => x.ProductNdc).HasMaxLength(16);
        builder.Property(x => x.GenericName).HasMaxLength(1000).IsRequired();
        builder.Property(x => x.BrandName).HasMaxLength(1000);
        builder.Property(x => x.DosageForm).HasMaxLength(200);
        builder.Property(x => x.Version).HasMaxLength(32).IsRequired();
        builder.HasIndex(x => new { x.IsActive, x.ProductNdc });
    }
}

public sealed class NdcVersionConfiguration : IEntityTypeConfiguration<NdcVersion>
{
    public void Configure(EntityTypeBuilder<NdcVersion> builder)
    {
        builder.ToTable("NdcVersions", "terminology"); builder.HasKey(x => x.Id);
        builder.Property(x => x.Version).HasMaxLength(32).IsRequired(); builder.Property(x => x.ChecksumSha256).HasMaxLength(64);
        builder.HasIndex(x => x.Version).IsUnique(); builder.HasIndex(x => x.IsActive).HasFilter("[IsActive] = 1").IsUnique();
    }
}

public sealed class NdcImportHistoryConfiguration : IEntityTypeConfiguration<NdcImportHistory>
{
    public void Configure(EntityTypeBuilder<NdcImportHistory> builder)
    {
        builder.ToTable("NdcImportHistory", "terminology"); builder.HasKey(x => x.Id);
        builder.Property(x => x.Version).HasMaxLength(32); builder.Property(x => x.ChecksumSha256).HasMaxLength(64); builder.Property(x => x.Status).HasMaxLength(32).IsRequired();
        builder.Property(x => x.ErrorMessage); builder.HasIndex(x => x.StartedOnUtc);
    }
}
