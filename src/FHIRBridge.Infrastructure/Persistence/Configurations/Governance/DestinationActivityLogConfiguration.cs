using FHIRBridge.Domain.Entities.Governance;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations.Governance;

public sealed class DestinationActivityLogConfiguration : IEntityTypeConfiguration<DestinationActivityLog>
{
    public void Configure(EntityTypeBuilder<DestinationActivityLog> builder)
    {
        builder.ToTable("DestinationActivityLogs");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.DestinationName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.DestinationType).HasMaxLength(50).IsRequired();
        builder.Property(x => x.Stage).HasMaxLength(20).IsRequired();
        builder.Property(x => x.Status).HasMaxLength(20).IsRequired();
        builder.Property(x => x.ResourceType).HasMaxLength(100);
        builder.Property(x => x.Detail).HasMaxLength(500);
        builder.Property(x => x.Error).HasMaxLength(1000);
        builder.Property(x => x.CorrelationId).HasMaxLength(100);

        // CorrelationId first: Correlation Search is this table's primary read path, and it filters on nothing else.
        builder.HasIndex(x => x.CorrelationId);
        builder.HasIndex(x => x.OccurredOnUtc);
        builder.HasIndex(x => x.PipelineRunId);
    }
}
