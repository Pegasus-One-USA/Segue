using FHIRBridge.Api.IntegrationTests.TestHelpers;
using FHIRBridge.Application.Abstractions.Licensing;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FHIRBridge.Api.IntegrationTests;

/// <summary>
/// Stub external-token validator: returns a canned <see cref="ExternalIdentity"/> so SSO endpoints can be
/// tested hermetically (no network to Entra/Google). The token string doubles as the returned email, which
/// lets a test drive the accept-invite-sso email-match guard by choosing the token value.
/// </summary>
public sealed class StubExternalTokenValidator : IExternalTokenValidator
{
    public const string CannedSubject = "sso-subject-123";
    public const string CannedName = "SSO User";

    public Task<ExternalIdentity> ValidateAsync(LoginProvider provider, string token, CancellationToken cancellationToken)
    {
        // The token is treated as "provider:email". A bare value is used verbatim as the email.
        var email = token.Contains(':') ? token[(token.IndexOf(':') + 1)..] : token;
        return Task.FromResult(new ExternalIdentity(provider, CannedSubject, email, CannedName));
    }
}

/// <summary>
/// The API host for the integration suite. Two modes:
/// <list type="bullet">
/// <item>default: no database at all — the host runs on its in-memory stores;</item>
/// <item><c>FHIRBRIDGE_IT_DB</c> set to a PostgreSQL connection string: the host runs against that database and
/// migrates it on startup. Point it ONLY at an empty, throwaway database (run-against-postgres.ps1 creates and
/// drops one): the fixture performs first-run setup and the tests create and delete rows.</item>
/// </list>
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string DatabaseEnvironmentVariable = "FHIRBRIDGE_IT_DB";

    /// <summary>The PostgreSQL connection string the suite runs against, or null for the in-memory mode.</summary>
    public static string? DatabaseConnectionString =>
        Environment.GetEnvironmentVariable(DatabaseEnvironmentVariable) is { Length: > 0 } value ? value : null;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var database = DatabaseConnectionString;

        // "Testing" prevents appsettings.Development.json from loading (which contains
        // the real SQL Server connection string). Program.cs sees an empty connection
        // string and self-selects the InMemory database path — no multi-provider conflict.
        builder.UseEnvironment("Testing");

        // UseSetting, not only the in-memory collection below: with minimal hosting, Program.cs reads the connection
        // string while it registers services, before ConfigureAppConfiguration overrides apply. Since the base
        // appsettings.json gained a local PostgreSQL default, only a setting applied this early keeps the host on
        // its in-memory stores instead of a real database, or on the throwaway database named by FHIRBRIDGE_IT_DB.
        builder.UseSetting("ConnectionStrings:FHIRBridgeDb", database ?? string.Empty);
        if (database is not null)
        {
            builder.UseSetting("Database:Provider", "PostgreSql");
            // Secrets go to the database-backed store, never to a real Key Vault.
            builder.UseSetting("KeyVault:UseAzureKeyVault", "false");
        }

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:FHIRBridgeDb"]           = database ?? "",
                // No Authentication:SigningKey here on purpose — AppSecretProvisioner auto-generates one via
                // the in-memory secret store (empty connection string above) on first use within this run.
                ["Authentication:TokenLifetimeMinutes"]     = "60",
                ["Authentication:RefreshTokenLifetimeDays"] = "7",
                ["LocalAuth:ExposeResetTokens"]             = "true",
                // Single-org bootstrap: no user is seeded. The RBAC catalog (roles/permissions) is provisioned
                // at startup (in-memory repo self-seed on this path), and the fixture creates the first
                // SuperAdmin via POST /api/v1/auth/setup-superadmin, then authenticates as that user.
                // Enable Google SSO in config so GET /config reports it; Entra stays disabled.
                ["Authentication:Google:Enabled"]           = "true",
                ["Authentication:Google:ClientId"]          = "test-google-client-id",
                // Disable rate limiting so the hermetic test suite (many requests from one client)
                // isn't throttled; enforcement is validated in production config, not here.
                ["RateLimiting:Enabled"]                    = "false"
            });
        });

        builder.ConfigureServices(services =>
        {
            if (database is null)
            {
                // InMemoryUserAccessRepository uses instance-level ConcurrentDictionary.
                // When registered as Scoped (Program.cs default) each HTTP request gets a
                // fresh empty store, so data written in one request is lost by the next.
                // Re-register as Singleton so the same instance (and its in-memory data)
                // lives for the full test run. With a database the EF store is used as is.
                var existing = services
                    .Where(d => d.ServiceType == typeof(IUserAccessRepository))
                    .ToList();
                foreach (var d in existing)
                    services.Remove(d);

                services.AddSingleton<IUserAccessRepository, InMemoryUserAccessRepository>();
            }

            // Replace the real Entra/Google validators with a hermetic stub (no network).
            var validators = services
                .Where(d => d.ServiceType == typeof(IExternalTokenValidator))
                .ToList();
            foreach (var d in validators)
                services.Remove(d);

            services.AddSingleton<IExternalTokenValidator, StubExternalTokenValidator>();

            // An always-active license, so the license gate admits the suite's calls (see ActiveTestLicenseService).
            foreach (var d in services.Where(d => d.ServiceType == typeof(ILicenseService)).ToList())
                services.Remove(d);

            services.AddSingleton<ILicenseService, ActiveTestLicenseService>();
        });
    }
}
