using FHIRBridge.Domain.Entities.Governance;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations.Governance;

public sealed class AlertHistoryEntryConfiguration : IEntityTypeConfiguration<AlertHistoryEntry>
{
    public void Configure(EntityTypeBuilder<AlertHistoryEntry> builder)
    {
        builder.ToTable("AlertHistoryEntries");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.RuleName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Severity).HasMaxLength(20).IsRequired();
        builder.Property(x => x.Summary).HasMaxLength(1000).IsRequired();
        builder.Property(x => x.AcknowledgedBy).HasMaxLength(320);

        builder.HasIndex(x => x.AlertRuleId);
        builder.HasIndex(x => x.FiredOnUtc);
    }
}
