using FHIRBridge.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations;

public sealed class SourceConnectionConfiguration : IEntityTypeConfiguration<SourceConnection>
{
    public void Configure(EntityTypeBuilder<SourceConnection> builder)
    {
        builder.ToTable("SourceConnections");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.TenantId).IsRequired();
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.SourceSystemType).HasConversion<string>().HasMaxLength(50).IsRequired();
        builder.Property(x => x.BaseUrl).HasMaxLength(500).IsRequired();
        builder.Property(x => x.IsEnabled).IsRequired();

        builder.OwnsOne(x => x.Authentication, authentication =>
        {
            authentication.Property(x => x.AuthenticationType)
                .HasConversion<string>()
                .HasMaxLength(100)
                .HasColumnName("AuthenticationType")
                .IsRequired();

            authentication.Property(x => x.ClientId)
                .HasMaxLength(300)
                .HasColumnName("ClientId");

            authentication.Property(x => x.TokenEndpoint)
                .HasMaxLength(500)
                .HasColumnName("TokenEndpoint");

            authentication.Property(x => x.KeyId)
                .HasMaxLength(200)
                .HasColumnName("KeyId");

            var scopesProperty = authentication.Property(x => x.Scopes)
                .HasConversion(
                    value => string.Join(' ', value),
                    value => value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .HasMaxLength(1000)
                .HasColumnName("Scopes");

            scopesProperty.Metadata.SetValueComparer(new ValueComparer<string[]>(
                (left, right) => ReferenceEquals(left, right) || (left != null && right != null && left.SequenceEqual(right)),
                value => value.Aggregate(0, (hash, item) => HashCode.Combine(hash, StringComparer.Ordinal.GetHashCode(item))),
                value => value.ToArray()));

            authentication.OwnsOne(x => x.ClientSecret, secret =>
            {
                secret.Property(x => x.KeyVaultName)
                    .HasMaxLength(200)
                    .HasColumnName("ClientSecretKeyVaultName");
                secret.Property(x => x.SecretName)
                    .HasMaxLength(200)
                    .HasColumnName("ClientSecretName");
            });

            authentication.OwnsOne(x => x.PrivateKey, secret =>
            {
                secret.Property(x => x.KeyVaultName)
                    .HasMaxLength(200)
                    .HasColumnName("PrivateKeyKeyVaultName");
                secret.Property(x => x.SecretName)
                    .HasMaxLength(200)
                    .HasColumnName("PrivateKeySecretName");
            });
        });
    }
}
