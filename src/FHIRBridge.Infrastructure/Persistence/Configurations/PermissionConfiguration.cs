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

        // Combined descriptions grow with every new endpoint sharing a permission code (see PermissionCatalog's
        // " | "-joining of every distinct [StandardPermission] description for the same code) — 500 chars was
        // already tight before this session's governance-endpoint additions pushed governance.read's combined
        // description past it (SQL error 2628 truncation on startup sync). 4000 gives real headroom as the
        // catalog keeps growing, not just enough for today's count.
        builder.Property(x => x.Description)
            .HasMaxLength(4000)
            .IsRequired();

        builder.Property(x => x.IsSystem).IsRequired();

        builder.Property(x => x.IsVisible).IsRequired();

        builder.Property(x => x.IsActive).IsRequired();

        // Same scaling problem as Description above: comma-joined "ClassName.MethodName" for every occurrence
        // of a shared permission code (governance.read is now used by 30+ endpoints) outgrew 1000 chars —
        // SQL error 2628 on Api startup. Widened generously for the same reason.
        builder.Property(x => x.Instances).HasMaxLength(4000);

        builder.HasOne<PermissionGroup>()
            .WithMany()
            .HasForeignKey(x => x.GroupId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(x => x.GroupId);

        // Not unique: Id (the primary key) is itself the deterministic encoding of this permission's
        // Group+Action pair (see PermissionTaxonomy.BuildPermissionId), so Id uniqueness already guarantees
        // at most one row per (Group, Action). A plain index here is purely for lookup performance — a
        // deactivated permission (removed from code, see RbacBootstrapper / Program.SyncDiscoveredPermissionsAsync)
        // can keep its historical Name for audit purposes while a fresh row with a corrected Id is created
        // under the same Name, with no constraint conflict.
        builder.HasIndex(x => x.Name);

        // Built-in permission rows are provisioned at runtime by IRbacBootstrapper (RbacSeedData),
        // not via migration HasData — a freshly-migrated empty database self-provisions them on boot.
    }
}
