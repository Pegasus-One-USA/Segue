using FHIRBridge.Domain.Entities.Governance;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations.Governance;

public sealed class EndpointHealthCheckConfiguration : IEntityTypeConfiguration<EndpointHealthCheck>
{
    public void Configure(EntityTypeBuilder<EndpointHealthCheck> builder)
    {
        builder.ToTable("EndpointHealthChecks");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.EndpointName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.EndpointType).HasMaxLength(50).IsRequired();
        builder.Property(x => x.Status).HasMaxLength(50).IsRequired();
        builder.Property(x => x.Message).HasMaxLength(1000);

        builder.HasIndex(x => x.OccurredOnUtc);
        builder.HasIndex(x => x.EndpointName);
        builder.HasIndex(x => x.Status);
    }
}
