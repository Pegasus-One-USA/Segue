using FHIRBridge.Domain.Entities.Governance;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations.Governance;

public sealed class AlertRuleConfiguration : IEntityTypeConfiguration<AlertRule>
{
    public void Configure(EntityTypeBuilder<AlertRule> builder)
    {
        builder.ToTable("AlertRules");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.EventTypeFilter).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Severity).HasMaxLength(20).IsRequired();
        builder.Property(x => x.Recipients).HasMaxLength(1000).IsRequired();
    }
}
