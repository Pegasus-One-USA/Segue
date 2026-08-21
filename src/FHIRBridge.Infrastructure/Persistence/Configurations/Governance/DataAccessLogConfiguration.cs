using FHIRBridge.Domain.Entities.Governance;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations.Governance;

public sealed class DataAccessLogConfiguration : IEntityTypeConfiguration<DataAccessLog>
{
    public void Configure(EntityTypeBuilder<DataAccessLog> builder)
    {
        builder.ToTable("DataAccessLogs");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Actor).HasMaxLength(320).IsRequired();
        builder.Property(x => x.ResourceType).HasMaxLength(100).IsRequired();
        builder.Property(x => x.ResourceId).HasMaxLength(256);
        builder.Property(x => x.Action).HasMaxLength(50).IsRequired();
        builder.Property(x => x.PatientId).HasMaxLength(256);
        builder.Property(x => x.Purpose).HasMaxLength(200);
        builder.Property(x => x.CorrelationId).HasMaxLength(100);
        builder.Property(x => x.IpAddress).HasMaxLength(64);

        builder.HasIndex(x => x.OccurredOnUtc);
        builder.HasIndex(x => x.CorrelationId);
        builder.HasIndex(x => x.PatientId);
        builder.HasIndex(x => x.PipelineRunId);
    }
}
