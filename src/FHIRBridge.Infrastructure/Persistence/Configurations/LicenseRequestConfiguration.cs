using FHIRBridge.Domain.Entities.Licensing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations;

public sealed class LicenseRequestConfiguration : IEntityTypeConfiguration<LicenseRequest>
{
    public void Configure(EntityTypeBuilder<LicenseRequest> builder)
    {
        builder.ToTable("LicenseRequests");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.ClientName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Email).HasMaxLength(320).IsRequired();
        builder.Property(x => x.CompanyName).HasMaxLength(200);
        builder.Property(x => x.Address).HasMaxLength(500);
        builder.Property(x => x.PhoneNumber).HasMaxLength(50).IsRequired();
        builder.Property(x => x.UniqueKey).HasMaxLength(64).IsRequired();
        builder.Property(x => x.RequestHost).HasMaxLength(255);
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(x => x.SubmissionError).HasMaxLength(500);

        // Unique, not just indexed — UniqueKey is generated fresh per submission and is what a minted
        // license's requestKey claim is matched against (see LicenseService.ApplyAsync), so two rows
        // must never share one.
        builder.HasIndex(x => x.UniqueKey).IsUnique();
    }
}
