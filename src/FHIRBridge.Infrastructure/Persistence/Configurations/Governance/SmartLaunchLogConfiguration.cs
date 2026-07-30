using FHIRBridge.Domain.Entities.Governance;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations.Governance;

public sealed class SmartLaunchLogConfiguration : IEntityTypeConfiguration<SmartLaunchLog>
{
    public void Configure(EntityTypeBuilder<SmartLaunchLog> builder)
    {
        builder.ToTable("SmartLaunchLogs");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.SourceName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.LaunchType).HasMaxLength(50).IsRequired();
        builder.Property(x => x.FailureReason).HasMaxLength(1000);
        builder.Property(x => x.GrantedScope).HasMaxLength(500);
        builder.Property(x => x.TokenCacheKeyHash).HasMaxLength(20);
        builder.Property(x => x.CorrelationId).HasMaxLength(100);

        builder.HasIndex(x => x.OccurredOnUtc);
        builder.HasIndex(x => x.SourceConnectionId);
        builder.HasIndex(x => x.CorrelationId);
    }
}
