using FHIRBridge.Domain.Entities.Terminology;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations.Terminology;

public sealed class CvxCodeConfiguration : IEntityTypeConfiguration<CvxCode>
{
    public void Configure(EntityTypeBuilder<CvxCode> builder)
    {
        builder.ToTable("CvxCodes", "terminology");
        builder.HasKey(x => x.Code);
        builder.Property(x => x.Code).HasMaxLength(16);
        builder.Property(x => x.ShortDescription).HasMaxLength(500).IsRequired();
        builder.Property(x => x.FullVaccineName).HasMaxLength(1000).IsRequired();
        builder.Property(x => x.Version).HasMaxLength(32).IsRequired();
        builder.HasIndex(x => new { x.IsActive, x.Code });
    }
}

public sealed class CvxVersionConfiguration : IEntityTypeConfiguration<CvxVersion>
{
    public void Configure(EntityTypeBuilder<CvxVersion> builder)
    {
        builder.ToTable("CvxVersions", "terminology"); builder.HasKey(x => x.Id);
        builder.Property(x => x.Version).HasMaxLength(32).IsRequired(); builder.Property(x => x.ChecksumSha256).HasMaxLength(64);
        builder.HasIndex(x => x.Version).IsUnique(); builder.HasIndex(x => x.IsActive).HasFilter("[IsActive] = 1").IsUnique();
    }
}

public sealed class CvxImportHistoryConfiguration : IEntityTypeConfiguration<CvxImportHistory>
{
    public void Configure(EntityTypeBuilder<CvxImportHistory> builder)
    {
        builder.ToTable("CvxImportHistory", "terminology"); builder.HasKey(x => x.Id);
        builder.Property(x => x.Version).HasMaxLength(32); builder.Property(x => x.ChecksumSha256).HasMaxLength(64); builder.Property(x => x.Status).HasMaxLength(32).IsRequired();
        builder.Property(x => x.ErrorMessage); builder.HasIndex(x => x.StartedOnUtc);
    }
}
