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

        builder.HasIndex(x => x.UniqueKey);

        // One-row-per-install, enforced at the schema level (not just LicenseRequestService's
        // GetAsync-then-insert check, which two concurrent POST /api/v1/license-request calls could
        // both pass before either commits): a shadow column fixed to the same constant value on every
        // row, with a plain (non-filtered) unique index on it. A second insert violates the index
        // regardless of that row's own real columns — this needs no provider-specific filtered-index
        // syntax, so it's identical across the SqlServer and PostgreSQL migration projects.
        // LicenseRequestService.CreateAndSubmitAsync catches the resulting DbUpdateException and turns
        // it into the same "already exists" InvalidOperationException the pre-check throws.
        builder.Property<int>("SingletonGuard").HasDefaultValue(1);
        builder.HasIndex("SingletonGuard").IsUnique();
    }
}
