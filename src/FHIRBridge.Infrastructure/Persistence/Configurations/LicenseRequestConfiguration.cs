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
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(x => x.SubmissionError).HasMaxLength(500);

        // Not filtered/unique-indexed on any column deliberately — this table is meant to hold at most one
        // row per install already (enforced in LicenseRequestService, not the schema), and keeping the
        // schema itself unconstrained avoids provider-specific filtered-index syntax differences between
        // the SqlServer and PostgreSQL migration projects for what is otherwise a single-row table.
        builder.HasIndex(x => x.UniqueKey);
    }
}
