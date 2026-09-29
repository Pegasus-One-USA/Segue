using FHIRBridge.LicenseServer.Domain;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.LicenseServer.Data;

/// <summary>
/// Single DbContext for this tool. Backed by a real PostgreSQL database (see
/// <c>ConnectionStrings:LicenseServerDb</c> and the <c>UseNpgsql</c> call in <c>Program.cs</c>) — a
/// separate, dedicated database on the same local/deployed Postgres server the main FHIRBridge product
/// uses, never the product's own <c>FHIRBridge</c> database. Schema is managed via real EF Core
/// migrations (see the <c>Migrations/</c> folder) applied through <c>Database.MigrateAsync()</c> at
/// startup, not <c>EnsureCreated()</c>.
/// </summary>
public sealed class LicenseServerDbContext : DbContext
{
    public LicenseServerDbContext(DbContextOptions<LicenseServerDbContext> options)
        : base(options)
    {
    }

    public DbSet<IssuedLicense> IssuedLicenses => Set<IssuedLicense>();

    public DbSet<Installation> Installations => Set<Installation>();

    public DbSet<CheckIn> CheckIns => Set<CheckIn>();

    public DbSet<LicenseRequest> LicenseRequests => Set<LicenseRequest>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<IssuedLicense>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ClaimsJson).HasColumnType("text");
            entity.Property(x => x.Token).HasColumnType("text");
            entity.HasIndex(x => x.IssuedAtUtc);
        });

        modelBuilder.Entity<Installation>(entity =>
        {
            entity.HasKey(x => x.InstallationId);
            entity.HasIndex(x => x.LastSeenUtc);
            entity.HasOne(x => x.CurrentIssuedLicense)
                .WithMany()
                .HasForeignKey(x => x.CurrentIssuedLicenseId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<CheckIn>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.InstallationId, x.ReceivedAtUtc });
            entity.HasOne<IssuedLicense>()
                .WithMany()
                .HasForeignKey(x => x.CurrentIssuedLicenseId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<LicenseRequest>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.UniqueKey).IsUnique();
            entity.HasIndex(x => x.FulfilledAtUtc);
            entity.HasOne(x => x.FulfilledIssuedLicense)
                .WithMany()
                .HasForeignKey(x => x.FulfilledIssuedLicenseId)
                .OnDelete(DeleteBehavior.SetNull);
        });
    }
}
