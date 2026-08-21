using FHIRBridge.Domain.Entities.Governance;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations.Governance;

public sealed class SchedulerHistoryConfiguration : IEntityTypeConfiguration<SchedulerHistory>
{
    public void Configure(EntityTypeBuilder<SchedulerHistory> builder)
    {
        builder.ToTable("SchedulerHistory");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.SchedulerId).HasMaxLength(500).IsRequired();
        builder.Property(x => x.Status).HasMaxLength(50).IsRequired();
        builder.Property(x => x.CorrelationId).HasMaxLength(100);

        builder.HasIndex(x => x.RunTimeUtc);
        builder.HasIndex(x => x.CorrelationId);
    }
}
