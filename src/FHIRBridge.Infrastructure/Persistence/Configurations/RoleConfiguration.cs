using FHIRBridge.Application.Security;
using FHIRBridge.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations;

public sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.ToTable("Roles");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Name)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.Description)
            .HasMaxLength(500)
            .IsRequired();

        builder.Property(x => x.IsEnabled).IsRequired();
        builder.Property(x => x.IsSystem).IsRequired();
        builder.Property(x => x.IsDefault).IsRequired();

        // Unique per tenant (and per the null-tenant platform scope), not globally — every tenant
        // gets its own SuperAdmin/Admin/Operations/Audit roles alongside the platform's.
        builder.HasIndex(x => new { x.TenantId, x.Name })
            .IsUnique();

        builder.HasData(
            RoleSeed(SeededSecurityIds.SuperAdminRoleId, UnifiedRoles.SuperAdmin, "Full platform administrator across all tenants."),
            RoleSeed(SeededSecurityIds.AdminRoleId, UnifiedRoles.Admin, "Administers configuration and users within a tenant."),
            RoleSeed(SeededSecurityIds.OperationsRoleId, UnifiedRoles.Operations, "Builds and runs pipeline configurations, and reviews data and audit output, within a tenant."),
            RoleSeed(SeededSecurityIds.AuditRoleId, UnifiedRoles.Audit, "Read-only access to configuration and audit logs."));
    }

    private static object RoleSeed(Guid id, string name, string description)
    {
        return new
        {
            Id = id,
            Name = name,
            Description = description,
            IsSystem = true,
            IsDefault = false,
            IsEnabled = true,
            IsDeleted = false,
            CreatedOnUtc = SeedConstants.SeedTimestamp
        };
    }
}
