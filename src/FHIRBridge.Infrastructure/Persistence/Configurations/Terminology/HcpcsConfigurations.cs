using FHIRBridge.Domain.Entities.Terminology;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations.Terminology;

public sealed class HcpcsCodeConfiguration : IEntityTypeConfiguration<HcpcsCode>
{
    public void Configure(EntityTypeBuilder<HcpcsCode> builder)
    {
        builder.ToTable("HcpcsCodes", "terminology");
        builder.HasKey(x => x.Code);
        builder.Property(x => x.Code).HasMaxLength(16);
        builder.Property(x => x.ShortDescription).HasMaxLength(500).IsRequired();
        builder.Property(x => x.LongDescription).IsRequired();
        builder.Property(x => x.Version).HasMaxLength(32).IsRequired();
        builder.HasIndex(x => new { x.IsActive, x.Code });
    }
}

public sealed class HcpcsVersionConfiguration : IEntityTypeConfiguration<HcpcsVersion>
{
    public void Configure(EntityTypeBuilder<HcpcsVersion> builder)
    {
        builder.ToTable("HcpcsVersions", "terminology"); builder.HasKey(x => x.Id);
        builder.Property(x => x.Version).HasMaxLength(32).IsRequired(); builder.Property(x => x.ChecksumSha256).HasMaxLength(64);
        builder.HasIndex(x => x.Version).IsUnique(); builder.HasIndex(x => x.IsActive).HasFilter("[IsActive] = 1").IsUnique();
    }
}

public sealed class HcpcsImportHistoryConfiguration : IEntityTypeConfiguration<HcpcsImportHistory>
{
    public void Configure(EntityTypeBuilder<HcpcsImportHistory> builder)
    {
        builder.ToTable("HcpcsImportHistory", "terminology"); builder.HasKey(x => x.Id);
        builder.Property(x => x.Version).HasMaxLength(32); builder.Property(x => x.ChecksumSha256).HasMaxLength(64); builder.Property(x => x.Status).HasMaxLength(32).IsRequired();
        builder.Property(x => x.ErrorMessage); builder.HasIndex(x => x.StartedOnUtc);
    }
}
