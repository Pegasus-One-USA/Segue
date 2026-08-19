using FHIRBridge.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations;

public sealed class TransformationRuleConfiguration : IEntityTypeConfiguration<TransformationRule>
{
    public void Configure(EntityTypeBuilder<TransformationRule> builder)
    {
        builder.ToTable("TransformationRules");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Scope).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(x => x.DestinationType).HasConversion<string>().HasMaxLength(30);
        builder.Property(x => x.ResourceType).HasMaxLength(100);
        builder.Property(x => x.DestinationField).HasMaxLength(200);
        builder.Property(x => x.SourceSystem).HasMaxLength(200);
        builder.Property(x => x.SourceField).HasMaxLength(500);
        builder.Property(x => x.NodeType).HasConversion<string>().HasMaxLength(40).IsRequired();
        builder.Property(x => x.ConfigJson).IsRequired();
        builder.Property(x => x.Order).IsRequired();
        builder.Property(x => x.OnNull).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(x => x.ErrorPolicy).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(x => x.OnNullDefaultValue).HasMaxLength(500);
        builder.Property(x => x.ArrayMode).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(x => x.FhirWriteBackJsonPath).HasMaxLength(500);
        builder.Property(x => x.ExecutionPhase).HasConversion<string>().HasMaxLength(20).IsRequired()
            .HasDefaultValue(FHIRBridge.Domain.Enums.TransformExecutionPhase.PostMapping);
        builder.Property(x => x.DeIdentificationProfileId);
        builder.Property(x => x.IsEnabled).IsRequired();

        // Speeds up the resolver's per-tier lookups (GetFieldScopedAsync/GetResourceTypeScopedAsync/etc.).
        builder.HasIndex(x => new { x.Scope, x.ResourceType, x.DestinationField, x.SourceSystem, x.SourceField });
        builder.HasIndex(x => new { x.Scope, x.DestinationType, x.DestinationField });
        builder.HasIndex(x => new { x.Scope, x.ResourcePipelineRouteId, x.ResourceType, x.DestinationField, x.SourceSystem, x.SourceField });
        // Speeds up SafeHarborDeIdentificationService's pre-mapping lookup by profile + resource type.
        builder.HasIndex(x => new { x.ExecutionPhase, x.DeIdentificationProfileId, x.ResourceType });
    }
}
