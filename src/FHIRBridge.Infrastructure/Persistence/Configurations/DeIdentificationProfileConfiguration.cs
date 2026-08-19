using FHIRBridge.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations;

public sealed class DeIdentificationProfileConfiguration : IEntityTypeConfiguration<DeIdentificationProfile>
{
    public void Configure(EntityTypeBuilder<DeIdentificationProfile> builder)
    {
        builder.ToTable("DeIdentificationProfiles");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(1000);

        builder.HasIndex(x => x.Name).IsUnique();
    }
}
