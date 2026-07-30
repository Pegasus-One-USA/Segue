using FHIRBridge.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations;

public sealed class SystemSettingConfiguration : IEntityTypeConfiguration<SystemSetting>
{
    public void Configure(EntityTypeBuilder<SystemSetting> builder)
    {
        builder.ToTable("SystemSettings");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Key).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Value).HasMaxLength(2000).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(500);

        // Filtered so a deleted key's slot can be reused, same rationale as AllowedCorsOriginConfiguration.
        builder.HasIndex(x => x.Key).IsUnique().HasFilter("[IsDeleted] = 0");
    }
}
