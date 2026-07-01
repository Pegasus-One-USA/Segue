using FHIRBridge.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations;

public sealed class ResourceLineageEntryConfiguration : IEntityTypeConfiguration<ResourceLineageEntry>
{
    public void Configure(EntityTypeBuilder<ResourceLineageEntry> builder)
    {
        builder.ToTable("ResourceLineageEntries");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.TenantId).IsRequired();
        builder.Property(x => x.PipelineRunId).IsRequired();
        builder.Property(x => x.RouteId);
        builder.Property(x => x.SourceConnectionId);
        builder.Property(x => x.DestinationId);
        builder.Property(x => x.MappingProfileId);
        builder.Property(x => x.ResourceType).HasMaxLength(100).IsRequired();
        builder.Property(x => x.SourceResourceId).HasMaxLength(256);
        builder.Property(x => x.Action).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Status).HasMaxLength(50).IsRequired();
        builder.Property(x => x.OccurredOnUtc).IsRequired();

        // Chain reconstruction queries filter by tenant + run/resource and order by time.
        builder.HasIndex(x => new { x.TenantId, x.OccurredOnUtc });
        builder.HasIndex(x => new { x.TenantId, x.PipelineRunId });
        builder.HasIndex(x => new { x.TenantId, x.SourceResourceId });
    }
}
