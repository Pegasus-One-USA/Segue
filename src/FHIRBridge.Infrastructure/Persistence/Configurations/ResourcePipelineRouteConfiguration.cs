using FHIRBridge.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations;

public sealed class ResourcePipelineRouteConfiguration : IEntityTypeConfiguration<ResourcePipelineRoute>
{
    public void Configure(EntityTypeBuilder<ResourcePipelineRoute> builder)
    {
        builder.ToTable("ResourcePipelineRoutes");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.TenantId).IsRequired();
        builder.Property(x => x.WebhookConfigurationId);
        builder.Property(x => x.MappingProfileId).IsRequired();
        builder.Property(x => x.IngestionMode).HasConversion<string>().HasMaxLength(100).IsRequired();
        builder.Property(x => x.ScheduleExpression).HasMaxLength(1000);
        builder.Property(x => x.SearchParameters).HasMaxLength(1000);
        builder.Property(x => x.IsEnabled).IsRequired();
        builder.Property(x => x.Priority).IsRequired();
        builder.Property(x => x.LastTriggeredOnUtc);

        builder.HasIndex(x => new
            {
                x.TenantId,
                x.WebhookConfigurationId,
                x.MappingProfileId
            })
            .IsUnique();

        builder.HasIndex(x => new { x.TenantId, x.MappingProfileId });

        builder.HasOne<WebhookConfiguration>()
            .WithMany()
            .HasForeignKey(x => x.WebhookConfigurationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<MappingProfile>()
            .WithMany()
            .HasForeignKey(x => x.MappingProfileId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
