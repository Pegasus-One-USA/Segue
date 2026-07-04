using FHIRBridge.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations;

public sealed class PermissionConfiguration : IEntityTypeConfiguration<Permission>
{
    public void Configure(EntityTypeBuilder<Permission> builder)
    {
        builder.ToTable("Permissions");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Name)
            .HasMaxLength(150)
            .IsRequired();

        builder.Property(x => x.DisplayName)
            .HasMaxLength(150)
            .IsRequired();

        builder.Property(x => x.Description)
            .HasMaxLength(500)
            .IsRequired();

        builder.Property(x => x.IsSystem).IsRequired();

        builder.Property(x => x.IsVisible).IsRequired();

        builder.HasOne<PermissionGroup>()
            .WithMany()
            .HasForeignKey(x => x.GroupId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(x => x.GroupId);

        builder.HasIndex(x => x.Name)
            .IsUnique();

        // Built-in permission rows are provisioned at runtime by IRbacBootstrapper (RbacSeedData),
        // not via migration HasData — a freshly-migrated empty database self-provisions them on boot.
    }
}
