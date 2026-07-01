using System.Text.Json;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations;

public sealed class SourceCapabilityProfileConfiguration : IEntityTypeConfiguration<SourceCapabilityProfile>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public void Configure(EntityTypeBuilder<SourceCapabilityProfile> builder)
    {
        builder.ToTable("SourceCapabilityProfiles");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.TenantId).IsRequired();
        builder.Property(x => x.SourceConnectionId).IsRequired();
        builder.Property(x => x.FhirVersion).HasMaxLength(20).IsRequired();
        builder.Property(x => x.DiscoveredOnUtc).IsRequired();
        builder.Property(x => x.RawCapabilityJson);

        // One snapshot per source connection; UpsertAsync relies on this uniqueness.
        builder.HasIndex(x => new { x.TenantId, x.SourceConnectionId }).IsUnique();

        var scopesProperty = builder.Property(x => x.ConfiguredScopes)
            .HasConversion(
                value => string.Join(' ', value),
                value => value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .HasMaxLength(2000)
            .HasColumnName("ConfiguredScopes");

        scopesProperty.Metadata.SetValueComparer(new ValueComparer<string[]>(
            (left, right) => ReferenceEquals(left, right) || (left != null && right != null && left.SequenceEqual(right)),
            value => value.Aggregate(0, (hash, item) => HashCode.Combine(hash, StringComparer.Ordinal.GetHashCode(item))),
            value => value.ToArray()));

        // The supported-resources list is stored as a JSON document. It is read/written wholesale per snapshot,
        // so a single JSON column is simpler than a child table and needs no extra Include on reads.
        var resourcesProperty = builder.Property(x => x.Resources)
            .HasConversion(
                value => JsonSerializer.Serialize(value, JsonOptions),
                value => (IReadOnlyList<CapabilityResource>)(JsonSerializer.Deserialize<List<CapabilityResource>>(value, JsonOptions)
                    ?? new List<CapabilityResource>()))
            .HasColumnName("ResourcesJson");

        resourcesProperty.Metadata.SetValueComparer(new ValueComparer<IReadOnlyList<CapabilityResource>>(
            (left, right) => ReferenceEquals(left, right) || (left != null && right != null && left.SequenceEqual(right)),
            value => value.Aggregate(0, (hash, item) => HashCode.Combine(hash, item.ResourceType.GetHashCode())),
            value => value.ToList()));
    }
}
