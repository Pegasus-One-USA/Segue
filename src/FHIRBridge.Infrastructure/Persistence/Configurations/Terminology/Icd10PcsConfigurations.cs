using FHIRBridge.Domain.Entities.Terminology;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations.Terminology;

public sealed class Icd10PcsCodeConfiguration : IEntityTypeConfiguration<Icd10PcsCode>
{
    public void Configure(EntityTypeBuilder<Icd10PcsCode> builder)
    {
        builder.ToTable("Icd10PcsCodes", "terminology");
        builder.HasKey(x => x.Code);
        builder.Property(x => x.Code).HasMaxLength(16);
        builder.Property(x => x.ShortDescription).HasMaxLength(500).IsRequired();
        builder.Property(x => x.LongDescription).HasColumnType("nvarchar(max)").IsRequired();
        builder.Property(x => x.Version).HasMaxLength(32).IsRequired();
        builder.HasIndex(x => new { x.IsActive, x.Code });
    }
}

public sealed class Icd10PcsVersionConfiguration : IEntityTypeConfiguration<Icd10PcsVersion>
{
    public void Configure(EntityTypeBuilder<Icd10PcsVersion> builder)
    {
        builder.ToTable("Icd10PcsVersions", "terminology"); builder.HasKey(x => x.Id);
        builder.Property(x => x.Version).HasMaxLength(32).IsRequired(); builder.Property(x => x.ChecksumSha256).HasMaxLength(64);
        builder.HasIndex(x => x.Version).IsUnique(); builder.HasIndex(x => x.IsActive).HasFilter("[IsActive] = 1").IsUnique();
    }
}

public sealed class Icd10PcsImportHistoryConfiguration : IEntityTypeConfiguration<Icd10PcsImportHistory>
{
    public void Configure(EntityTypeBuilder<Icd10PcsImportHistory> builder)
    {
        builder.ToTable("Icd10PcsImportHistory", "terminology"); builder.HasKey(x => x.Id);
        builder.Property(x => x.Version).HasMaxLength(32); builder.Property(x => x.ChecksumSha256).HasMaxLength(64); builder.Property(x => x.Status).HasMaxLength(32).IsRequired();
        builder.Property(x => x.ErrorMessage).HasColumnType("nvarchar(max)"); builder.HasIndex(x => x.StartedOnUtc);
    }
}
