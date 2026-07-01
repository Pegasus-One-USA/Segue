using FHIRBridge.Domain.Aggregates;
using FHIRBridge.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations;

public sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.ToTable("Tenants");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Name)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(x => x.Code)
            .HasMaxLength(50)
            .IsRequired();

        builder.HasIndex(x => x.Code)
            .IsUnique();

        builder.Property(x => x.Status)
            .HasConversion<string>()
            .HasMaxLength(50)
            .IsRequired();

        builder.HasMany(x => x.SourceConnections)
            .WithOne()
            .HasForeignKey(x => x.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(x => x.WebhookConfigurations)
            .WithOne()
            .HasForeignKey(x => x.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(x => x.DestinationConfigurations)
            .WithOne()
            .HasForeignKey(x => x.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(x => x.MappingProfiles)
            .WithOne()
            .HasForeignKey(x => x.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(x => x.ResourcePipelineRoutes)
            .WithOne()
            .HasForeignKey(x => x.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(x => x.SourceConnections)
            .UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(x => x.WebhookConfigurations)
            .UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(x => x.DestinationConfigurations)
            .UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(x => x.MappingProfiles)
            .UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(x => x.ResourcePipelineRoutes)
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
