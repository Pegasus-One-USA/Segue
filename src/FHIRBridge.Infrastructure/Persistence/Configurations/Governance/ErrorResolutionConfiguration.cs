using FHIRBridge.Domain.Entities.Governance;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations.Governance;

public sealed class ErrorResolutionConfiguration : IEntityTypeConfiguration<ErrorResolution>
{
    public void Configure(EntityTypeBuilder<ErrorResolution> builder)
    {
        builder.ToTable("ErrorResolutions");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.ErrorReferenceId).HasMaxLength(40).IsRequired();
        builder.Property(x => x.Status).HasMaxLength(20).IsRequired();
        builder.Property(x => x.ResolvedBy).HasMaxLength(256);
        builder.Property(x => x.Notes).HasMaxLength(2000);

        // One resolution row per error reference.
        builder.HasIndex(x => x.ErrorReferenceId).IsUnique();
    }
}
