using FHIRBridge.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations;

public sealed class PipelineRunRouteExecutionConfiguration : IEntityTypeConfiguration<PipelineRunRouteExecution>
{
    public void Configure(EntityTypeBuilder<PipelineRunRouteExecution> builder)
    {
        builder.ToTable("PipelineRunRouteExecutions");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.PipelineName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.SourceName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.SourceSystemType).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Status).HasMaxLength(50).IsRequired();
        builder.Property(x => x.TriggeredBy).HasMaxLength(200);
        builder.Property(x => x.TriggerType).HasMaxLength(50);
        builder.Property(x => x.ErrorMessage).HasMaxLength(2000);
        builder.Property(x => x.StartedOnUtc).IsRequired();

        builder.HasIndex(x => x.PipelineRunId);
        builder.HasIndex(x => x.Status);
        builder.HasIndex(x => x.StartedOnUtc);
    }
}
