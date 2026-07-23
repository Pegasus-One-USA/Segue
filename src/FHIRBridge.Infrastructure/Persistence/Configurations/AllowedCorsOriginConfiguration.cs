using FHIRBridge.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations;

public sealed class AllowedCorsOriginConfiguration : IEntityTypeConfiguration<AllowedCorsOrigin>
{
    public void Configure(EntityTypeBuilder<AllowedCorsOrigin> builder)
    {
        builder.ToTable("AllowedCorsOrigins");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.OriginUrl).HasMaxLength(500).IsRequired();
        builder.Property(x => x.Label).HasMaxLength(200);

        // Filtered to non-deleted rows so a re-added origin can reuse the same value as one that was
        // soft-deleted earlier — otherwise the deleted row's index entry would permanently block it.
        builder.HasIndex(x => x.OriginUrl).IsUnique().HasFilter("[IsDeleted] = 0");
    }
}
