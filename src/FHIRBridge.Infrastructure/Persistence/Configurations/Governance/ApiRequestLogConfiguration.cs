using FHIRBridge.Domain.Entities.Governance;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations.Governance;

public sealed class ApiRequestLogConfiguration : IEntityTypeConfiguration<ApiRequestLog>
{
    public void Configure(EntityTypeBuilder<ApiRequestLog> builder)
    {
        builder.ToTable("ApiRequestLogs");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Method).HasMaxLength(10).IsRequired();
        builder.Property(x => x.Url).HasMaxLength(1000).IsRequired();
        builder.Property(x => x.Error).HasMaxLength(1000);
        builder.Property(x => x.CorrelationId).HasMaxLength(100);
        builder.Property(x => x.Direction).HasMaxLength(10).IsRequired()
            .HasDefaultValue(ApiRequestDirection.Outbound);

        builder.HasIndex(x => x.OccurredOnUtc);
        builder.HasIndex(x => x.CorrelationId);
        builder.HasIndex(x => x.StatusCode);
        // Inbound rows are far more numerous than outbound ones (every portal poll lands here), so the common
        // "one attempt's calls, in order" lookup filters on both columns together.
        builder.HasIndex(x => new { x.CorrelationId, x.Direction });
    }
}
