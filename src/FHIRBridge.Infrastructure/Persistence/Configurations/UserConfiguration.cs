using FHIRBridge.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations;

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("Users");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.ExternalUserId)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(x => x.Email)
            .HasMaxLength(320);

        builder.Property(x => x.DisplayName)
            .HasMaxLength(200);

        builder.Property(x => x.PasswordHash)
            .HasMaxLength(500);

        builder.Property(x => x.IsLocalLoginEnabled)
            .IsRequired();

        builder.Property(x => x.MustChangePassword)
            .IsRequired();

        builder.Property(x => x.PasswordResetTokenHash)
            .HasMaxLength(500);

        builder.Property(x => x.FirstName)
            .HasMaxLength(100);

        builder.Property(x => x.LastName)
            .HasMaxLength(100);

        builder.Property(x => x.IsEnabled)
            .IsRequired();

        builder.Property(x => x.FailedLoginCount)
            .IsRequired();

        builder.Property(x => x.MfaEnabled)
            .IsRequired();

        // MFA (TOTP) enrollment state. Secret is Base32; backup codes are stored as newline-joined hashes.
        builder.Property(x => x.MfaSecret)
            .HasMaxLength(200);

        builder.Property(x => x.MfaBackupCodeHashes)
            .HasMaxLength(4000);

        // Login-time MFA challenge (issued after password verification, consumed by the follow-up code submission).
        builder.Property(x => x.MfaChallengeTokenHash)
            .HasMaxLength(500);

        builder.Property(x => x.MustSetupMfa)
            .IsRequired();

        builder.Property(x => x.CreatedOnUtc)
            .IsRequired();

        // UserStatus enum stored as int column.
        builder.Property(x => x.Status)
            .IsRequired();

        // LoginProvider enum stored as a readable string column (Local / Entra / Google).
        builder.Property(x => x.LoginProvider)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        // Invitation flow.
        builder.Property(x => x.InvitationTokenHash)
            .HasMaxLength(500);

        // Refresh token.
        builder.Property(x => x.RefreshTokenHash)
            .HasMaxLength(500);

        // Defaults existing rows (issued before this column existed, when every refresh cookie was
        // unconditionally persistent) to true — the migration must not silently downgrade any
        // already-remembered session to session-only. A plain CLR property initializer isn't enough
        // on its own: EF's migration scaffolding reads the column's default from this Fluent
        // configuration, not from the C# `= true` on the property itself.
        builder.Property(x => x.RefreshTokenRememberMe)
            .HasDefaultValue(true);

        builder.Property(x => x.TenantId)
            .IsRequired();

        // No CLR navigation property on either side (User has none back to Tenant, Tenant has no
        // ICollection<User>) — a pure FK-only relationship is fully supported by EF Core and avoids
        // adding a collection navigation to Tenant that nothing actually needs today. RESTRICT (not
        // Cascade) is deliberate: deleting a tenant must never silently delete its users — see
        // TenantsService.DeleteAsync's explicit HasUsersAsync check, which is the intended way a
        // tenant-with-users deletion is blocked; RESTRICT is the DB-level backstop behind that check.
        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(x => x.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        // Filtered by soft-delete for the same reason IX_EhrEndpoints_Vendor_VendorEndpointId and
        // IX_AllowedCorsOrigins_OriginUrl are: without the filter, soft-deleting a user (e.g. cleaning up
        // a stray/orphaned invite) leaves its ExternalUserId — deterministically "local:{email}" for local
        // accounts — permanently reserved in the DB-level unique index, even though the app-level duplicate
        // check (which does respect the global IsDeleted query filter) reports the email as free. Re-inviting
        // that exact email then always fails with an opaque 500 (Postgres 23505 on IX_Users_ExternalUserId)
        // that the invite guard's friendly "already exists" message never gets a chance to catch.
        builder.HasIndex(x => x.ExternalUserId)
            .IsUnique()
            .HasFilter("\"IsDeleted\" = false");

        builder.HasIndex(x => x.Email);

        builder.HasIndex(x => x.RefreshTokenHash);

        builder.HasIndex(x => x.MfaChallengeTokenHash);

        builder.HasIndex(x => x.TenantId);
    }
}
