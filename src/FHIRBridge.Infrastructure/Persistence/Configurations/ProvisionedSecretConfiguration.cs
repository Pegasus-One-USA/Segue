using FHIRBridge.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations;

public sealed class ProvisionedSecretConfiguration : IEntityTypeConfiguration<ProvisionedSecret>
{
    public void Configure(EntityTypeBuilder<ProvisionedSecret> builder)
    {
        builder.ToTable("ProvisionedSecrets");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.KeyVaultName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.SecretName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.ProtectedValue).HasColumnType("nvarchar(max)").IsRequired();
        builder.Property(x => x.CreatedOnUtc).IsRequired();
        builder.Property(x => x.ModifiedOnUtc).IsRequired();

        builder.HasIndex(x => new { x.KeyVaultName, x.SecretName }).IsUnique();
    }
}
