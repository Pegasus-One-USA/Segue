using FHIRBridge.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations;

public sealed class NotificationSettingsConfig : IEntityTypeConfiguration<NotificationSettings>
{
    public void Configure(EntityTypeBuilder<NotificationSettings> builder)
    {
        builder.ToTable("NotificationSettings");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Host).HasMaxLength(255).IsRequired();
        builder.Property(x => x.Username).HasMaxLength(255);
        builder.Property(x => x.FromAddress).HasMaxLength(255).IsRequired();
        builder.Property(x => x.FromName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.IsEnabled).IsRequired();
        builder.Property(x => x.Port).IsRequired();
        builder.Property(x => x.EnableSsl).IsRequired();

        builder.OwnsOne(x => x.PasswordSecretReference, secret =>
        {
            secret.Property(x => x.KeyVaultName)
                .HasMaxLength(200)
                .HasColumnName("PasswordKeyVaultName");
            secret.Property(x => x.SecretName)
                .HasMaxLength(200)
                .HasColumnName("PasswordSecretName");
        });
    }
}
