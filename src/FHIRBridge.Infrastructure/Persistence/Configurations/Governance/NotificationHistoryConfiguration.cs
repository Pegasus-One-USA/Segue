using FHIRBridge.Domain.Entities.Governance;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations.Governance;

public sealed class NotificationHistoryConfiguration : IEntityTypeConfiguration<NotificationHistory>
{
    public void Configure(EntityTypeBuilder<NotificationHistory> builder)
    {
        builder.ToTable("NotificationHistory");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.NotificationType).HasMaxLength(50).IsRequired();
        builder.Property(x => x.Recipient).HasMaxLength(1000).IsRequired();
        builder.Property(x => x.Subject).HasMaxLength(500);
        builder.Property(x => x.Status).HasMaxLength(50).IsRequired();
        builder.Property(x => x.Error).HasMaxLength(1000);
        builder.Property(x => x.CorrelationId).HasMaxLength(100);

        builder.HasIndex(x => x.OccurredOnUtc);
        builder.HasIndex(x => x.CorrelationId);
    }
}
