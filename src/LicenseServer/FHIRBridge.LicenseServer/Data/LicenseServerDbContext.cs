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

    public DbSet<ErrorReportImport> ErrorReportImports => Set<ErrorReportImport>();

    public DbSet<ErrorReportEntry> ErrorReportEntries => Set<ErrorReportEntry>();

    public DbSet<ErrorReportCorrelation> ErrorReportCorrelations => Set<ErrorReportCorrelation>();

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

        modelBuilder.Entity<ErrorReportImport>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(200);
            entity.Property(x => x.ClientName).HasMaxLength(200);
            entity.Property(x => x.ImportedBy).HasMaxLength(200);
            entity.Property(x => x.SourceFileName).HasMaxLength(300);
            entity.Property(x => x.Format).HasMaxLength(10);
            entity.Property(x => x.ApplicationVersion).HasMaxLength(400);
            entity.HasIndex(x => x.ImportedAtUtc);
            entity.HasIndex(x => x.ClientName);
            entity.HasMany(x => x.Entries)
                .WithOne(x => x.Import)
                .HasForeignKey(x => x.ImportId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ErrorReportCorrelation>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.CorrelationId).HasMaxLength(150);
            entity.Property(x => x.RunStatus).HasMaxLength(60);
            entity.Property(x => x.EventsJson).HasColumnType("text");
            entity.HasIndex(x => new { x.ImportId, x.CorrelationId });
            entity.HasOne(x => x.Import)
                .WithMany(x => x.Correlations)
                .HasForeignKey(x => x.ImportId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ErrorReportEntry>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Message).HasColumnType("text");
            entity.Property(x => x.StackTrace).HasColumnType("text");
            entity.Property(x => x.Cause).HasMaxLength(1000);
            entity.Property(x => x.WorkflowName).HasMaxLength(200);
            entity.Property(x => x.NodeName).HasMaxLength(200);
            entity.Property(x => x.NodeType).HasMaxLength(200);
            entity.Property(x => x.SourceName).HasMaxLength(200);
            entity.Property(x => x.DestinationName).HasMaxLength(200);
            entity.Property(x => x.ResourceType).HasMaxLength(200);
            entity.HasIndex(x => new { x.ImportId, x.OccurredOnUtc });
        });
    }
}
