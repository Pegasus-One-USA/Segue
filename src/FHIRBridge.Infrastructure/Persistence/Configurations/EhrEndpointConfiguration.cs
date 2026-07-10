using FHIRBridge.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations;

public sealed class EhrEndpointConfiguration : IEntityTypeConfiguration<EhrEndpoint>
{
    public void Configure(EntityTypeBuilder<EhrEndpoint> builder)
    {
        builder.ToTable("EhrEndpoints");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Vendor).IsRequired();
        builder.Property(x => x.VendorEndpointId).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Name).HasMaxLength(300).IsRequired();
        builder.Property(x => x.FhirBaseUrl).HasMaxLength(500).IsRequired();
        builder.Property(x => x.FormatType).HasMaxLength(20).IsRequired();
        builder.Property(x => x.Status).HasMaxLength(50).IsRequired();

        // Unique per vendor, not globally — different vendors could coincidentally reuse an id scheme.
        builder.HasIndex(x => new { x.Vendor, x.VendorEndpointId }).IsUnique();
        builder.HasIndex(x => x.Name);

        // Rows are provisioned at runtime by vendor-specific IEhrEndpointDirectorySeeder implementations
        // (e.g. EpicEndpointDirectorySeeder, fetched from https://open.epic.com/Endpoints/R4), not via migration HasData.
    }
}
