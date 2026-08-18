using FHIRBridge.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations;

public sealed class DestinationConfigurationConfig : IEntityTypeConfiguration<DestinationConfiguration>
{
    public void Configure(EntityTypeBuilder<DestinationConfiguration> builder)
    {
        builder.ToTable("DestinationConfigurations");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.DestinationType).HasConversion<string>().HasMaxLength(50).IsRequired();
        builder.Property(x => x.Target).HasMaxLength(500);
        builder.Property(x => x.IsEnabled).IsRequired();
        builder.Property(x => x.RequiresDeIdentification).IsRequired().HasDefaultValue(false);
        builder.Property(x => x.DeIdentificationMethod).HasMaxLength(50);

        builder.OwnsOne(x => x.SecretReference, secret =>
        {
            secret.Property(x => x.KeyVaultName)
                .HasMaxLength(200)
                .HasColumnName("KeyVaultName")
                .IsRequired();
            secret.Property(x => x.SecretName)
                .HasMaxLength(200)
                .HasColumnName("SecretName")
                .IsRequired();
        });
    }
}
