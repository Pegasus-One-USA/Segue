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

        builder.Property(x => x.WebhookConfigurationId);
        builder.Property(x => x.MappingProfileId).IsRequired();
        builder.Property(x => x.IngestionMode).HasConversion<string>().HasMaxLength(100).IsRequired();
        builder.Property(x => x.ScheduleExpression).HasMaxLength(1000);
        builder.Property(x => x.SearchParameters).HasMaxLength(1000);
        builder.Property(x => x.IsEnabled).IsRequired();
        builder.Property(x => x.Priority).IsRequired();
        builder.Property(x => x.LastTriggeredOnUtc);

        builder.Metadata.FindNavigation(nameof(ResourcePipelineRoute.ResourceMappings))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);

        builder.HasIndex(x => new
            {
                x.WebhookConfigurationId,
                x.MappingProfileId
            })
            .IsUnique();

        builder.HasIndex(x => x.MappingProfileId);

        builder.HasOne<WebhookConfiguration>()
            .WithMany()
            .HasForeignKey(x => x.WebhookConfigurationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<MappingProfile>()
            .WithMany()
            .HasForeignKey(x => x.MappingProfileId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.OwnsMany(x => x.ResourceMappings, mapping =>
        {
            mapping.ToTable("ResourcePipelineRouteMappings");
            mapping.HasKey(x => x.Id);
            mapping.Property(x => x.Id).ValueGeneratedNever();
            mapping.Property(x => x.ResourcePipelineRouteId).IsRequired();
            mapping.Property(x => x.MappingProfileId).IsRequired();
            mapping.Property(x => x.IsEnabled).IsRequired();
            mapping.Property(x => x.ExecutionOrder).IsRequired();
            mapping.Property(x => x.SearchParameters).HasMaxLength(1000);
            mapping.WithOwner().HasForeignKey(x => x.ResourcePipelineRouteId);
            mapping.HasIndex(x => new { x.ResourcePipelineRouteId, x.MappingProfileId }).IsUnique();
            mapping.HasIndex(x => x.MappingProfileId);
            mapping.HasOne<MappingProfile>()
                .WithMany()
                .HasForeignKey(x => x.MappingProfileId)
                .OnDelete(DeleteBehavior.Restrict);

            mapping.OwnsMany(x => x.ParentReferences, parent =>
            {
                parent.ToTable("ResourcePipelineRouteMappingParentReferences");
                parent.HasKey(x => x.Id);
                parent.Property(x => x.Id).ValueGeneratedNever();
                parent.Property(x => x.ResourcePipelineRouteMappingId).IsRequired();
                parent.Property(x => x.ParentMappingProfileId).IsRequired();
                parent.Property(x => x.ReferenceFieldOverride).HasMaxLength(500);
                parent.WithOwner().HasForeignKey(x => x.ResourcePipelineRouteMappingId);
                parent.HasIndex(x => new { x.ResourcePipelineRouteMappingId, x.ParentMappingProfileId }).IsUnique();
                // No FK to MappingProfile: ParentMappingProfileId must resolve to a mapping inside this
                // specific route (the route's primary mapping or another entry in ResourceMappings), which a
                // table-level FK can't express — validated in ConfigurationService instead.
            });

            mapping.Navigation(x => x.ParentReferences).UsePropertyAccessMode(PropertyAccessMode.Field);
        });
    }
}
