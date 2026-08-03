using FHIRBridge.Domain.Entities.Governance;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations.Governance;

public sealed class RetryHistoryConfiguration : IEntityTypeConfiguration<RetryHistory>
{
    public void Configure(EntityTypeBuilder<RetryHistory> builder)
    {
        builder.ToTable("RetryHistory");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Context).HasMaxLength(500).IsRequired();
        builder.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
        builder.Property(x => x.CorrelationId).HasMaxLength(100);

        builder.HasIndex(x => x.OccurredOnUtc);
        builder.HasIndex(x => x.CorrelationId);
    }
}
