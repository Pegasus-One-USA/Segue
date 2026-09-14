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

        // RBAC redesign "Full System Access" capability flag — see Role.IsFullAccess doc comment. Read by
        // CachedUserPermissionsProvider (folded into the effective-permission-code set) and by the
        // UnifiedAdminAuthorizationHandler/SuperAdminOnlyAuthorizationHandler authorization handlers;
        // grant/revoke is gated through RoleManagementService. Defaults false, backfilled true only for
        // SuperAdmin/Admin via the AddRoleIsFullAccess migration.
        builder.Property(x => x.IsFullAccess).IsRequired().HasDefaultValue(false);

        // Role names are unique among non-deleted rows only — filtered so a soft-deleted role's name can
        // be reused by a new role, matching AllowedCorsOriginConfiguration/EhrEndpointConfiguration's own
        // pattern. Without this, DeleteRoleAsync's soft delete leaves the old row's name permanently
        // occupying the index, and re-creating a role with that same name fails with a duplicate-key
        // error on IX_Roles_Name even though the old role no longer shows up anywhere in the UI.
        builder.HasIndex(x => x.Name)
            .IsUnique()
            .HasFilter("[IsDeleted] = 0");

        // Built-in system roles are provisioned at runtime by IRbacBootstrapper (RbacSeedData),
        // not via migration HasData — a freshly-migrated empty database self-provisions them on boot.
    }
}
