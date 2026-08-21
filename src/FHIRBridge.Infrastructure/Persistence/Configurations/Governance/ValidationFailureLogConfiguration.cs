using FHIRBridge.Domain.Entities.Governance;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations.Governance;

public sealed class ValidationFailureLogConfiguration : IEntityTypeConfiguration<ValidationFailureLog>
{
    public void Configure(EntityTypeBuilder<ValidationFailureLog> builder)
    {
        builder.ToTable("ValidationFailureLogs");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.ResourceType).HasMaxLength(100).IsRequired();
        builder.Property(x => x.ResourceId).HasMaxLength(256);
        builder.Property(x => x.WarningsJson).HasMaxLength(4000).IsRequired();
        builder.Property(x => x.CorrelationId).HasMaxLength(100);

        builder.HasIndex(x => x.OccurredOnUtc);
        builder.HasIndex(x => x.CorrelationId);
        builder.HasIndex(x => x.ResourceType);
    }
}
