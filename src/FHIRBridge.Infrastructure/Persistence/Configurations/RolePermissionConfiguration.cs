using FHIRBridge.Application.Security;
using FHIRBridge.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations;

public sealed class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> builder)
    {
        builder.ToTable("RolePermissions");
        builder.HasKey(x => new { x.RoleId, x.PermissionId });

        builder.HasOne<Role>()
            .WithMany()
            .HasForeignKey(x => x.RoleId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Permission>()
            .WithMany()
            .HasForeignKey(x => x.PermissionId)
            .OnDelete(DeleteBehavior.Cascade);

        var rolePermissions = UnifiedRolePermissionSeed.Grants
            .SelectMany(grant => grant.Value.Select(permissionId => Link(grant.Key, permissionId)))
            .ToArray();

        builder.HasData(rolePermissions);
    }

    private static object Link(Guid roleId, Guid permissionId)
    {
        return new
        {
            RoleId = roleId,
            PermissionId = permissionId,
            IsEnabled = true
        };
    }
}
