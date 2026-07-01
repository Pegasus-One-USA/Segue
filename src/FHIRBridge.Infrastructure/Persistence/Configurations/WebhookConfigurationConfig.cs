using FHIRBridge.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations;

public sealed class WebhookConfigurationConfig : IEntityTypeConfiguration<WebhookConfiguration>
{
    public void Configure(EntityTypeBuilder<WebhookConfiguration> builder)
    {
        builder.ToTable("WebhookConfigurations");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.TenantId).IsRequired();
        builder.Property(x => x.SourceConnectionId).IsRequired();
        builder.Property(x => x.ResourceType).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Path).HasMaxLength(500).IsRequired();
        builder.Property(x => x.IsEnabled).IsRequired();

        builder.HasIndex(x => new { x.TenantId, x.SourceConnectionId, x.ResourceType })
            .IsUnique();

        builder.HasIndex(x => new { x.TenantId, x.Path });
    }
}
