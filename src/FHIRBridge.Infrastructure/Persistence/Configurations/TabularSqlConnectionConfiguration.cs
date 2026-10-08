using FHIRBridge.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations;

public sealed class TabularSqlConnectionConfiguration : IEntityTypeConfiguration<TabularSqlConnection>
{
    public void Configure(EntityTypeBuilder<TabularSqlConnection> builder)
    {
        builder.ToTable("TabularSqlConnections");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Engine).HasMaxLength(32).IsRequired();
        builder.Property(x => x.KeyVaultName).HasMaxLength(256).IsRequired();
        builder.Property(x => x.SecretName).HasMaxLength(256).IsRequired();
        builder.Property(x => x.CreatedBy).HasMaxLength(256);
        builder.Property(x => x.CreatedOnUtc).IsRequired();
        builder.Property(x => x.UpdatedOnUtc).IsRequired();

        builder.HasIndex(x => x.Name).IsUnique().HasDatabaseName("UX_TabularSqlConnections_Name");
    }
}
