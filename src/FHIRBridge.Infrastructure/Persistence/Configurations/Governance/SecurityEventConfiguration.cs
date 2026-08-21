using FHIRBridge.Domain.Entities.Governance;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations.Governance;

public sealed class SecurityEventConfiguration : IEntityTypeConfiguration<SecurityEvent>
{
    public void Configure(EntityTypeBuilder<SecurityEvent> builder)
    {
        builder.ToTable("SecurityEvents");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Severity).HasMaxLength(20).IsRequired();
        builder.Property(x => x.EventType).HasMaxLength(100).IsRequired();
        builder.Property(x => x.UserEmail).HasMaxLength(320);
        builder.Property(x => x.IpAddress).HasMaxLength(64);
        builder.Property(x => x.Details).HasMaxLength(1000);
        builder.Property(x => x.CorrelationId).HasMaxLength(100);

        builder.HasIndex(x => x.OccurredOnUtc);
        builder.HasIndex(x => x.Severity);
        builder.HasIndex(x => x.Resolved);
    }
}
