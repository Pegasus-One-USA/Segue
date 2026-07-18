using FHIRBridge.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations;

public sealed class ConfiguredPipelineRunRecordConfiguration : IEntityTypeConfiguration<ConfiguredPipelineRunRecord>
{
    public void Configure(EntityTypeBuilder<ConfiguredPipelineRunRecord> builder)
    {
        builder.ToTable("ConfiguredPipelineRuns");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Status).HasMaxLength(50).IsRequired();
        builder.Property(x => x.ResourceTypes).HasMaxLength(1000).IsRequired();
        builder.Property(x => x.ExtractedResourceCount).IsRequired();
        builder.Property(x => x.MappedRecordCount).IsRequired();
        builder.Property(x => x.WrittenRecordCount).IsRequired();
        builder.Property(x => x.Errors).HasColumnType("nvarchar(max)").IsRequired();
        builder.Property(x => x.StartedOnUtc).IsRequired();
        builder.Property(x => x.CompletedOnUtc).IsRequired();
        builder.Property(x => x.IsEnabled).IsRequired();
        builder.Property(x => x.CorrelationId).HasMaxLength(100);

        builder.HasIndex(x => x.StartedOnUtc);
        builder.HasIndex(x => x.Status);
        builder.HasIndex(x => x.CorrelationId);
    }
}
