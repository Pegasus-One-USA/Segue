using FHIRBridge.Domain.Entities.Terminology;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations.Terminology;

public sealed class Icd10CodeConfiguration : IEntityTypeConfiguration<Icd10Code>
{
    public void Configure(EntityTypeBuilder<Icd10Code> builder)
    {
        builder.ToTable("Icd10Codes", "terminology");
        builder.HasKey(x => x.Code);
        builder.Property(x => x.Code).HasMaxLength(16);
        builder.Property(x => x.ShortDescription).HasMaxLength(500).IsRequired();
        builder.Property(x => x.LongDescription).HasMaxLength(2000).IsRequired();
        builder.Property(x => x.Version).HasMaxLength(32).IsRequired();
        builder.HasIndex(x => new { x.IsActive, x.Code });
    }
}

public sealed class Icd10VersionConfiguration : IEntityTypeConfiguration<Icd10Version>
{
    public void Configure(EntityTypeBuilder<Icd10Version> builder)
    {
        builder.ToTable("Icd10Versions", "terminology"); builder.HasKey(x => x.Id);
        builder.Property(x => x.Version).HasMaxLength(32).IsRequired(); builder.Property(x => x.ChecksumSha256).HasMaxLength(64);
        builder.HasIndex(x => x.Version).IsUnique(); builder.HasIndex(x => x.IsActive).HasFilter("[IsActive] = 1").IsUnique();
    }
}

public sealed class Icd10ImportHistoryConfiguration : IEntityTypeConfiguration<Icd10ImportHistory>
{
    public void Configure(EntityTypeBuilder<Icd10ImportHistory> builder)
    {
        builder.ToTable("Icd10ImportHistory", "terminology"); builder.HasKey(x => x.Id);
        builder.Property(x => x.Version).HasMaxLength(32); builder.Property(x => x.ChecksumSha256).HasMaxLength(64); builder.Property(x => x.Status).HasMaxLength(32).IsRequired();
        builder.Property(x => x.ErrorMessage); builder.HasIndex(x => x.StartedOnUtc);
    }
}
