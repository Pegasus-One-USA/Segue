using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FHIRBridge.Api.IntegrationTests;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // "Testing" prevents appsettings.Development.json from loading (which contains
        // the real SQL Server connection string). Program.cs sees an empty connection
        // string and self-selects the InMemory database path — no multi-provider conflict.
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"]     = "",
                ["Authentication:SigningKey"]               = "IntegrationTest-HS256-SigningKey-MustBeAtLeast32Chars!",
                ["Authentication:TokenLifetimeMinutes"]     = "60",
                ["Authentication:RefreshTokenLifetimeDays"] = "7",
                ["LocalAuth:ExposeResetTokens"]             = "true"
            });
        });

        builder.ConfigureServices(services =>
        {
            // InMemoryUserAccessRepository uses instance-level ConcurrentDictionary.
            // When registered as Scoped (Program.cs default) each HTTP request gets a
            // fresh empty store, so data written in one request is lost by the next.
            // Re-register as Singleton so the same instance (and its in-memory data)
            // lives for the full test run.
            var existing = services
                .Where(d => d.ServiceType == typeof(IUserAccessRepository))
                .ToList();
            foreach (var d in existing)
                services.Remove(d);

            services.AddSingleton<IUserAccessRepository, InMemoryUserAccessRepository>();
        });
    }
}
