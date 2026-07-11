using FHIRBridge.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations;

public sealed class PermissionCategoryConfiguration : IEntityTypeConfiguration<PermissionCategory>
{
    public void Configure(EntityTypeBuilder<PermissionCategory> builder)
    {
        builder.ToTable("PermissionCategories");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Name)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.DisplayName)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.Description).HasMaxLength(500);

        builder.Property(x => x.IsVisible).IsRequired();

        builder.HasIndex(x => x.Name)
            .IsUnique();

        // Built-in category rows are provisioned at runtime by IRbacBootstrapper (RbacSeedData),
        // not via migration HasData.
    }
}
