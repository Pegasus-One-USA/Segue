using FHIRBridge.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations;

public sealed class OperationalAuditLogConfiguration : IEntityTypeConfiguration<OperationalAuditLog>
{
    public void Configure(EntityTypeBuilder<OperationalAuditLog> builder)
    {
        builder.ToTable("OperationalAuditLogs");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Action).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Status).HasMaxLength(50).IsRequired();
        builder.Property(x => x.Message).HasMaxLength(2000).IsRequired();
        builder.Property(x => x.ResourceType).HasMaxLength(100);
        builder.Property(x => x.TriggeredBy).HasMaxLength(320);
        builder.Property(x => x.CorrelationId).HasMaxLength(100);
        builder.Property(x => x.OccurredOnUtc).IsRequired();

        builder.HasIndex(x => x.OccurredOnUtc);
    }
}
