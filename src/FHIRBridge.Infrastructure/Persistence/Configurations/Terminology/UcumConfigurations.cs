using FHIRBridge.Domain.Entities.Terminology;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations.Terminology;

public sealed class UcumUnitConfiguration : IEntityTypeConfiguration<UcumUnit>
{
    public void Configure(EntityTypeBuilder<UcumUnit> builder)
    {
        builder.ToTable("UcumUnits", "terminology");
        builder.HasKey(x => x.Code);
        builder.Property(x => x.Code).HasMaxLength(64);
        builder.Property(x => x.Name).HasMaxLength(500).IsRequired();
        builder.Property(x => x.PrintSymbol).HasMaxLength(64);
        builder.Property(x => x.Version).HasMaxLength(32).IsRequired();
        builder.HasIndex(x => new { x.IsActive, x.Code });
    }
}

public sealed class UcumVersionConfiguration : IEntityTypeConfiguration<UcumVersion>
{
    public void Configure(EntityTypeBuilder<UcumVersion> builder)
    {
        builder.ToTable("UcumVersions", "terminology"); builder.HasKey(x => x.Id);
        builder.Property(x => x.Version).HasMaxLength(32).IsRequired(); builder.Property(x => x.ChecksumSha256).HasMaxLength(64);
        builder.HasIndex(x => x.Version).IsUnique(); builder.HasIndex(x => x.IsActive).HasFilter("[IsActive] = 1").IsUnique();
    }
}

public sealed class UcumImportHistoryConfiguration : IEntityTypeConfiguration<UcumImportHistory>
{
    public void Configure(EntityTypeBuilder<UcumImportHistory> builder)
    {
        builder.ToTable("UcumImportHistory", "terminology"); builder.HasKey(x => x.Id);
        builder.Property(x => x.Version).HasMaxLength(32); builder.Property(x => x.ChecksumSha256).HasMaxLength(64); builder.Property(x => x.Status).HasMaxLength(32).IsRequired();
        builder.Property(x => x.ErrorMessage); builder.HasIndex(x => x.StartedOnUtc);
    }
}
