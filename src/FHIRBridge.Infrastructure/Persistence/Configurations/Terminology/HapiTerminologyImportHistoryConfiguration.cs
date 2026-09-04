using FHIRBridge.Domain.Entities.Terminology;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations.Terminology;

public sealed class HapiTerminologyImportHistoryConfiguration : IEntityTypeConfiguration<HapiTerminologyImportHistory>
{
    public void Configure(EntityTypeBuilder<HapiTerminologyImportHistory> builder)
    {
        builder.ToTable("HapiTerminologyImportHistory", "terminology");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.CodeSystem).HasMaxLength(32).IsRequired();
        builder.Property(x => x.Version).HasMaxLength(32);
        builder.Property(x => x.Status).HasMaxLength(32).IsRequired();
        builder.HasIndex(x => new { x.CodeSystem, x.StartedOnUtc });
    }
}
