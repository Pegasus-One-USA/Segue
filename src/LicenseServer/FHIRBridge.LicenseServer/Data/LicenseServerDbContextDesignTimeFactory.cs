using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FHIRBridge.LicenseServer.Data;

/// <summary>
/// Design-time factory used only by <c>dotnet ef migrations</c>/<c>database update</c> tooling to resolve a
/// connection string for <see cref="LicenseServerDbContext"/> outside of <c>Program.cs</c>'s DI container
/// (and without running this app's own startup database-creation/migration logic). Mirrors the shape of the
/// main FHIRBridge repo's <c>FHIRBridgePostgreSqlDesignTimeFactory</c>, though this project has no
/// reference into that repo. Not used at runtime.
/// </summary>
public sealed class LicenseServerDbContextDesignTimeFactory : IDesignTimeDbContextFactory<LicenseServerDbContext>
{
    public LicenseServerDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("LICENSESERVER_DB")
            ?? "Host=localhost;Port=5432;Database=FHIRBridgeLicenseServer;Username=postgres;Password=r00t1Pa$$2026;";

        var options = new DbContextOptionsBuilder<LicenseServerDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new LicenseServerDbContext(options);
    }
}
