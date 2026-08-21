using FHIRBridge.Domain.Entities.Governance;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations.Governance;

public sealed class ExportHistoryConfiguration : IEntityTypeConfiguration<ExportHistory>
{
    public void Configure(EntityTypeBuilder<ExportHistory> builder)
    {
        builder.ToTable("ExportHistory");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.DestinationName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Format).HasMaxLength(50).IsRequired();
        builder.Property(x => x.Status).HasMaxLength(50).IsRequired();
        builder.Property(x => x.CorrelationId).HasMaxLength(100);

        builder.HasIndex(x => x.OccurredOnUtc);
        builder.HasIndex(x => x.CorrelationId);
        builder.HasIndex(x => x.PipelineRunId);
    }
}
