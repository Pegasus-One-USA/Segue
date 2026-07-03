using FHIRBridge.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations;

public sealed class PermissionAllocationConfiguration : IEntityTypeConfiguration<PermissionAllocation>
{
    public void Configure(EntityTypeBuilder<PermissionAllocation> builder)
    {
        builder.ToTable("PermissionAllocations", tb =>
            tb.HasCheckConstraint(
                "CK_PermissionAllocations_RoleXorUser",
                "([RoleId] IS NOT NULL AND [UserId] IS NULL) OR ([RoleId] IS NULL AND [UserId] IS NOT NULL)"));

        builder.HasKey(x => x.Id);
        builder.Property(x => x.IsEnabled).IsRequired();

        builder.HasOne<Role>()
            .WithMany()
            .HasForeignKey(x => x.RoleId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Permission>()
            .WithMany()
            .HasForeignKey(x => x.PermissionId)
            .OnDelete(DeleteBehavior.Cascade);

        // RoleId/UserId are nullable, so SQL Server naturally excludes NULLs from these composite
        // unique indexes — a role-level row and a user-level row for the same permission never collide.
        builder.HasIndex(x => new { x.RoleId, x.PermissionId }).IsUnique();
        builder.HasIndex(x => new { x.UserId, x.PermissionId }).IsUnique();

        // Built-in role→permission grants are provisioned at runtime by IRbacBootstrapper
        // (RbacSeedData/UnifiedRolePermissionSeed); per-user allocations are never seeded here.
    }
}
