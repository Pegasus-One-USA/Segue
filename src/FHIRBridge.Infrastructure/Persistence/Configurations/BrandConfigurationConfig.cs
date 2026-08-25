using FHIRBridge.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations;

public sealed class BrandConfigurationConfig : IEntityTypeConfiguration<BrandConfiguration>
{
    public void Configure(EntityTypeBuilder<BrandConfiguration> builder)
    {
        builder.ToTable("BrandConfigurations");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.TenantId).IsRequired();

        // FK-only, no CLR navigation on either side (same reasoning as User -> Tenant). Cascade is
        // deliberate here (unlike User -> Tenant's Restrict) — a tenant's branding row has no purpose
        // once the tenant itself is deleted, and TenantsService.DeleteAsync already blocks deleting a
        // tenant that still has USERS, which is the actually-destructive case; branding rows are
        // disposable by comparison.
        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(x => x.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        // At most one branding row per tenant — this is the DB-level guarantee behind "changing Tenant
        // A's branding must never affect Tenant B's".
        builder.HasIndex(x => x.TenantId).IsUnique();

        builder.Property(x => x.CompanyName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.PrimaryColor).HasMaxLength(7).IsRequired();
        builder.Property(x => x.SecondaryColor).HasMaxLength(7).IsRequired();
        builder.Property(x => x.AccentColor).HasMaxLength(7).IsRequired();
        builder.Property(x => x.BackgroundColor).HasMaxLength(7).IsRequired();
        builder.Property(x => x.FontFamily).HasMaxLength(200);
        builder.Property(x => x.FooterText).HasMaxLength(500);
        builder.Property(x => x.SupportEmail).HasMaxLength(255);
        builder.Property(x => x.SupportPhone).HasMaxLength(50);
        builder.Property(x => x.Website).HasMaxLength(500);
        builder.Property(x => x.EmailFooterText).HasMaxLength(500);
        builder.Property(x => x.DefaultThemeMode).HasMaxLength(20).IsRequired();
        builder.Property(x => x.LoaderStyle).HasMaxLength(20).IsRequired();

        // Asset fields deliberately have NO HasMaxLength — the portal encodes uploaded images as data: URIs
        // client-side (no separate blob-storage upload endpoint), which can run to hundreds of KB. No
        // HasMaxLength maps to nvarchar(max) on SQL Server.
        builder.Property(x => x.LogoUrl);
        builder.Property(x => x.DarkLogoUrl);
        builder.Property(x => x.FaviconUrl);
        builder.Property(x => x.LoginBackgroundUrl);
        builder.Property(x => x.LoginIllustrationUrl);
        builder.Property(x => x.EmailLogoUrl);
    }
}
