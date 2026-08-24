using FHIRBridge.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations;

public sealed class TenantConfig : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.ToTable("Tenants");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Code).HasMaxLength(50).IsRequired();
        builder.Property(x => x.IsActive).IsRequired();

        // Case-insensitive uniqueness on Code — the pre-login ?tenant=<code> lookup and the migration's
        // "default" code both depend on there being exactly one match regardless of casing. SQL Server's
        // default collation (Latin1_General_CI_AS) is already case-insensitive, so a plain unique index
        // is sufficient without an explicit collation override.
        builder.HasIndex(x => x.Code).IsUnique();
    }
}
