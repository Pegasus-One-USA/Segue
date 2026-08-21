using FHIRBridge.Domain.Entities.Terminology;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations.Terminology;

public sealed class LoincConceptConfiguration : IEntityTypeConfiguration<LoincConcept>
{
    public void Configure(EntityTypeBuilder<LoincConcept> builder)
    {
        builder.ToTable("LoincConcepts", "terminology");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Code).HasMaxLength(32).IsRequired();
        builder.Property(x => x.Display).HasMaxLength(500);
        builder.Property(x => x.LongCommonName).HasMaxLength(1000);
        builder.Property(x => x.Version).HasMaxLength(32);
        foreach (var property in new[] { nameof(LoincConcept.Class), nameof(LoincConcept.Component), nameof(LoincConcept.Property), nameof(LoincConcept.TimeAspect), nameof(LoincConcept.System), nameof(LoincConcept.Scale), nameof(LoincConcept.Method), nameof(LoincConcept.Status) })
            builder.Property(property).HasMaxLength(500);
        builder.HasIndex(x => x.Code).IsUnique();
        builder.HasIndex(x => new { x.IsActive, x.Code });
    }
}

public sealed class LoincPartConfiguration : IEntityTypeConfiguration<LoincPart>
{
    public void Configure(EntityTypeBuilder<LoincPart> builder)
    {
        builder.ToTable("LoincParts", "terminology"); builder.HasKey(x => x.Id);
        builder.Property(x => x.PartNumber).HasMaxLength(64).IsRequired(); builder.Property(x => x.Version).HasMaxLength(32).IsRequired();
        builder.HasIndex(x => new { x.PartNumber, x.Version }).IsUnique();
    }
}

public sealed class LoincGroupConfiguration : IEntityTypeConfiguration<LoincGroup>
{
    public void Configure(EntityTypeBuilder<LoincGroup> builder)
    {
        builder.ToTable("LoincGroups", "terminology"); builder.HasKey(x => x.Id);
        builder.Property(x => x.GroupId).HasMaxLength(64).IsRequired(); builder.Property(x => x.Version).HasMaxLength(32).IsRequired();
        builder.HasIndex(x => new { x.GroupId, x.Version }).IsUnique();
    }
}

public sealed class LoincAnswerListConfiguration : IEntityTypeConfiguration<LoincAnswerList>
{
    public void Configure(EntityTypeBuilder<LoincAnswerList> builder)
    {
        builder.ToTable("LoincAnswerLists", "terminology"); builder.HasKey(x => x.Id);
        builder.Property(x => x.AnswerListId).HasMaxLength(64).IsRequired(); builder.Property(x => x.Version).HasMaxLength(32).IsRequired();
        builder.HasIndex(x => new { x.AnswerListId, x.AnswerCode, x.Version }).IsUnique();
    }
}

public sealed class LoincConceptMapConfiguration : IEntityTypeConfiguration<LoincConceptMap>
{
    public void Configure(EntityTypeBuilder<LoincConceptMap> builder)
    {
        builder.ToTable("LoincConceptMaps", "terminology"); builder.HasKey(x => x.Id);
        builder.Property(x => x.SourceSystem).HasMaxLength(500).IsRequired(); builder.Property(x => x.SourceCode).HasMaxLength(128).IsRequired();
        builder.Property(x => x.TargetSystem).HasMaxLength(500).IsRequired(); builder.Property(x => x.TargetCode).HasMaxLength(128).IsRequired(); builder.Property(x => x.Version).HasMaxLength(32).IsRequired();
        builder.HasIndex(x => new { x.SourceSystem, x.SourceCode, x.TargetSystem, x.Version });
    }
}

public sealed class LoincVersionConfiguration : IEntityTypeConfiguration<LoincVersion>
{
    public void Configure(EntityTypeBuilder<LoincVersion> builder)
    {
        builder.ToTable("LoincVersions", "terminology"); builder.HasKey(x => x.Id);
        builder.Property(x => x.Version).HasMaxLength(32).IsRequired(); builder.Property(x => x.ChecksumSha256).HasMaxLength(64);
        builder.HasIndex(x => x.Version).IsUnique(); builder.HasIndex(x => x.IsActive).HasFilter("[IsActive] = 1").IsUnique();
    }
}

public sealed class LoincImportHistoryConfiguration : IEntityTypeConfiguration<LoincImportHistory>
{
    public void Configure(EntityTypeBuilder<LoincImportHistory> builder)
    {
        builder.ToTable("LoincImportHistory", "terminology"); builder.HasKey(x => x.Id);
        builder.Property(x => x.Version).HasMaxLength(32); builder.Property(x => x.ChecksumSha256).HasMaxLength(64); builder.Property(x => x.Status).HasMaxLength(32).IsRequired();
        builder.Property(x => x.ErrorMessage).HasColumnType("nvarchar(max)"); builder.HasIndex(x => x.StartedOnUtc);
    }
}
