using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FHIRBridge.Infrastructure.Persistence;

/// <summary>
/// Design-time factory so EF Core tooling (migrations) can construct the context without
/// booting the API host. Not used at runtime.
/// </summary>
public sealed class FHIRBridgeDbContextDesignTimeFactory : IDesignTimeDbContextFactory<FHIRBridgeDbContext>
{
    public FHIRBridgeDbContext CreateDbContext(string[] args)
    {
        // Design-time only. Honors FHIRBRIDGE_DB if set, else the local dev connection from appsettings.
        var connectionString =
            Environment.GetEnvironmentVariable("FHIRBRIDGE_DB")
            ?? "Server=localhost,1433;Database=FHIRBridge;User Id=sa;Password=Your_password123;TrustServerCertificate=True;Encrypt=False;";

        var options = new DbContextOptionsBuilder<FHIRBridgeDbContext>()
            .UseSqlServer(connectionString)
            .Options;

        return new FHIRBridgeDbContext(options);
    }
}
