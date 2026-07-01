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

        builder.Property(x => x.CreatedOnUtc)
            .IsRequired();

        builder.HasIndex(x => x.ExternalUserId)
            .IsUnique();

        builder.HasIndex(x => x.Email);

        builder.HasIndex(x => x.TenantId);
    }
}
