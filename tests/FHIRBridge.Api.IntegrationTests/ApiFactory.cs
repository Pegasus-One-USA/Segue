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
                ["ConnectionStrings:FHIRBridgeDb"]           = "",
                ["Authentication:SigningKey"]               = "IntegrationTest-HS256-SigningKey-MustBeAtLeast32Chars!",
                ["Authentication:TokenLifetimeMinutes"]     = "60",
                ["Authentication:RefreshTokenLifetimeDays"] = "7",
                ["LocalAuth:ExposeResetTokens"]             = "true",
                // Single-org bootstrap: no user is seeded. The RBAC catalog (roles/permissions) is provisioned
                // at startup (in-memory repo self-seed on this path), and the fixture creates the first
                // SuperAdmin via POST /api/v1/auth/setup-superadmin, then authenticates as that user.
                // Enable Google SSO in config so GET /config reports it; Entra stays disabled.
                ["Authentication:Google:Enabled"]           = "true",
                ["Authentication:Google:ClientId"]          = "test-google-client-id"
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

            // Replace the real Entra/Google validators with a hermetic stub (no network).
            var validators = services
                .Where(d => d.ServiceType == typeof(IExternalTokenValidator))
                .ToList();
            foreach (var d in validators)
                services.Remove(d);

            services.AddSingleton<IExternalTokenValidator, StubExternalTokenValidator>();
        });
    }
}
