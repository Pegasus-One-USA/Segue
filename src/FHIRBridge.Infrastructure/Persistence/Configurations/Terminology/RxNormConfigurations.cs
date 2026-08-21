using FHIRBridge.Domain.Entities.Terminology;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations.Terminology;

public sealed class RxNormConceptConfiguration : IEntityTypeConfiguration<RxNormConcept>
{
    public void Configure(EntityTypeBuilder<RxNormConcept> builder)
    {
        builder.ToTable("RxNormConcepts", "terminology");
        builder.HasKey(x => x.Rxcui);
        builder.Property(x => x.Rxcui).HasMaxLength(16);
        builder.Property(x => x.Name).HasMaxLength(3000).IsRequired();
        builder.Property(x => x.TermType).HasMaxLength(20);
        builder.Property(x => x.Version).HasMaxLength(32).IsRequired();
        builder.HasIndex(x => new { x.IsActive, x.Rxcui });
    }
}

public sealed class RxNormVersionConfiguration : IEntityTypeConfiguration<RxNormVersion>
{
    public void Configure(EntityTypeBuilder<RxNormVersion> builder)
    {
        builder.ToTable("RxNormVersions", "terminology"); builder.HasKey(x => x.Id);
        builder.Property(x => x.Version).HasMaxLength(32).IsRequired(); builder.Property(x => x.ChecksumSha256).HasMaxLength(64);
        builder.HasIndex(x => x.Version).IsUnique(); builder.HasIndex(x => x.IsActive).HasFilter("[IsActive] = 1").IsUnique();
    }
}

public sealed class RxNormImportHistoryConfiguration : IEntityTypeConfiguration<RxNormImportHistory>
{
    public void Configure(EntityTypeBuilder<RxNormImportHistory> builder)
    {
        builder.ToTable("RxNormImportHistory", "terminology"); builder.HasKey(x => x.Id);
        builder.Property(x => x.Version).HasMaxLength(32); builder.Property(x => x.ChecksumSha256).HasMaxLength(64); builder.Property(x => x.Status).HasMaxLength(32).IsRequired();
        builder.Property(x => x.ErrorMessage).HasColumnType("nvarchar(max)"); builder.HasIndex(x => x.StartedOnUtc);
    }
}
