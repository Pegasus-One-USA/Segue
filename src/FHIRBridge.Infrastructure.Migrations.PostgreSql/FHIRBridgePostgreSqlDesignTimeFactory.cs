using FHIRBridge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FHIRBridge.Infrastructure.Migrations.PostgreSql;

/// <summary>
/// Design-time factory used only by `dotnet ef migrations` tooling to scaffold PostgreSQL migrations for
/// <see cref="FHIRBridgeDbContext"/> into this project's own migrations history (kept separate from the
/// SqlServer migrations in FHIRBridge.Infrastructure — EF Core does not allow two ModelSnapshots for the
/// same DbContext in one assembly). Not used at runtime; the runtime provider selection lives in
/// FHIRBridge.Infrastructure/DependencyInjection.cs.
/// </summary>
public sealed class FHIRBridgePostgreSqlDesignTimeFactory : IDesignTimeDbContextFactory<FHIRBridgeDbContext>
{
    public FHIRBridgeDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("FHIRBRIDGE_DB")
            ?? "Host=localhost;Port=5434;Database=FHIRBridge;Username=postgres;Password=Your_password123;";

        var options = new DbContextOptionsBuilder<FHIRBridgeDbContext>()
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsAssembly("FHIRBridge.Infrastructure.Migrations.PostgreSql"))
            .Options;

        return new FHIRBridgeDbContext(options);
    }
}
