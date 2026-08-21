using FHIRBridge.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations;

public sealed class SchemaMappingConfiguration : IEntityTypeConfiguration<SchemaMapping>
{
    public void Configure(EntityTypeBuilder<SchemaMapping> builder)
    {
        builder.ToTable("SchemaMappings");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.SourceSystem).HasMaxLength(200).IsRequired();
        builder.Property(x => x.ResourceType).HasMaxLength(100).IsRequired();
        builder.Property(x => x.DestinationTable).HasMaxLength(300).IsRequired();
        builder.Property(x => x.SourceField).HasMaxLength(500).IsRequired();
        builder.Property(x => x.DestinationField).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Confidence).IsRequired();
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();

        // One row per destination field per (source system, resource type, destination table) — matches the
        // lookup key SaveApprovedMappingAsync/GetApprovedAsync query against.
        builder.HasIndex(x => new { x.SourceSystem, x.ResourceType, x.DestinationTable, x.DestinationField })
            .IsUnique();
    }
}
