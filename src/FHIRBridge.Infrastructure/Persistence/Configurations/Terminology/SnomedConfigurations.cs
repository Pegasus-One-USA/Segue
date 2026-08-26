using FHIRBridge.Domain.Entities.Terminology;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations.Terminology;

public sealed class SnomedConceptConfiguration : IEntityTypeConfiguration<SnomedConcept>
{
    public void Configure(EntityTypeBuilder<SnomedConcept> builder)
    {
        builder.ToTable("SnomedConcepts", "terminology");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasMaxLength(18);
        builder.Property(x => x.ModuleId).HasMaxLength(18).IsRequired();
        builder.Property(x => x.DefinitionStatusId).HasMaxLength(18).IsRequired();
        builder.Property(x => x.Fsn).HasMaxLength(1000);
        builder.Property(x => x.PreferredTerm).HasMaxLength(1000);
        builder.Property(x => x.Version).HasMaxLength(32).IsRequired();
        builder.HasIndex(x => new { x.Active, x.Id });
    }
}

public sealed class SnomedDescriptionConfiguration : IEntityTypeConfiguration<SnomedDescription>
{
    public void Configure(EntityTypeBuilder<SnomedDescription> builder)
    {
        builder.ToTable("SnomedDescriptions", "terminology");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasMaxLength(18);
        builder.Property(x => x.ConceptId).HasMaxLength(18).IsRequired();
        builder.Property(x => x.Term).HasMaxLength(1000).IsRequired();
        builder.Property(x => x.TypeId).HasMaxLength(18).IsRequired();
        builder.Property(x => x.LanguageCode).HasMaxLength(8).IsRequired();
        builder.Property(x => x.CaseSignificanceId).HasMaxLength(18).IsRequired();
        builder.Property(x => x.Version).HasMaxLength(32).IsRequired();
        builder.HasIndex(x => x.ConceptId);
    }
}

public sealed class SnomedRelationshipConfiguration : IEntityTypeConfiguration<SnomedRelationship>
{
    public void Configure(EntityTypeBuilder<SnomedRelationship> builder)
    {
        builder.ToTable("SnomedRelationships", "terminology");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasMaxLength(18);
        builder.Property(x => x.SourceId).HasMaxLength(18).IsRequired();
        builder.Property(x => x.DestinationId).HasMaxLength(18).IsRequired();
        builder.Property(x => x.TypeId).HasMaxLength(18).IsRequired();
        builder.Property(x => x.CharacteristicTypeId).HasMaxLength(18).IsRequired();
        builder.Property(x => x.Version).HasMaxLength(32).IsRequired();
        builder.HasIndex(x => x.SourceId);
        builder.HasIndex(x => x.DestinationId);
    }
}

public sealed class SnomedVersionConfiguration : IEntityTypeConfiguration<SnomedVersion>
{
    public void Configure(EntityTypeBuilder<SnomedVersion> builder)
    {
        builder.ToTable("SnomedVersions", "terminology"); builder.HasKey(x => x.Id);
        builder.Property(x => x.Version).HasMaxLength(32).IsRequired(); builder.Property(x => x.ChecksumSha256).HasMaxLength(64);
        builder.HasIndex(x => x.Version).IsUnique(); builder.HasIndex(x => x.IsActive).HasFilter("[IsActive] = 1").IsUnique();
    }
}

public sealed class SnomedImportHistoryConfiguration : IEntityTypeConfiguration<SnomedImportHistory>
{
    public void Configure(EntityTypeBuilder<SnomedImportHistory> builder)
    {
        builder.ToTable("SnomedImportHistory", "terminology"); builder.HasKey(x => x.Id);
        builder.Property(x => x.Version).HasMaxLength(32); builder.Property(x => x.ChecksumSha256).HasMaxLength(64); builder.Property(x => x.Status).HasMaxLength(32).IsRequired();
        builder.Property(x => x.ErrorMessage); builder.HasIndex(x => x.StartedOnUtc);
    }
}
