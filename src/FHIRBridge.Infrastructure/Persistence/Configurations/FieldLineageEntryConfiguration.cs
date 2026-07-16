using FHIRBridge.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations;

public sealed class FieldLineageEntryConfiguration : IEntityTypeConfiguration<FieldLineageEntry>
{
    public void Configure(EntityTypeBuilder<FieldLineageEntry> builder)
    {
        builder.ToTable("FieldLineageEntries");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.PipelineRunId).IsRequired();
        builder.Property(x => x.MappingProfileId);
        builder.Property(x => x.ResourceType).HasMaxLength(100).IsRequired();
        builder.Property(x => x.SourceResourceId).HasMaxLength(256);
        builder.Property(x => x.SourceFieldPath).HasMaxLength(500).IsRequired();
        builder.Property(x => x.TransformationType).HasMaxLength(100).IsRequired();
        builder.Property(x => x.DestinationObject).HasMaxLength(200);
        builder.Property(x => x.DestinationColumn).HasMaxLength(200).IsRequired();
        builder.Property(x => x.OccurredOnUtc).IsRequired();

        // Drill-down queries filter by resource (and sometimes run) and order by time.
        builder.HasIndex(x => x.OccurredOnUtc);
        builder.HasIndex(x => x.PipelineRunId);
        builder.HasIndex(x => x.SourceResourceId);
    }
}
