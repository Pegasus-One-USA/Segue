using FHIRBridge.LicenseServer.Api;
using FHIRBridge.LicenseServer.Data;
using FHIRBridge.LicenseServer.Licensing;
using FHIRBridge.LicenseServer.Security;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.EventLog;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// A rolling file sink alongside the console — the ONLY place this service's logs go once deployed as a
// Windows Service: there's no attached console there, and the Event Log provider is deliberately
// silenced below (see that call's own remarks). Without this, a misbehaving deployed service would have
// nowhere to be diagnosed from at all. Path is relative to the app's own published folder, so it works
// identically whether run as a service or a plain console/`dotnet run` process.
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(AppContext.BaseDirectory, "Logs", "licenseserver-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14));

// No-op unless the process is actually started by the Windows Service Control Manager (e.g. `dotnet run`
// and console execution are unaffected) — lets the same published output run standalone or as a service.
// Without this, Windows Service Control Manager reports a bare "CouldNotStartService" on Start-Service:
// the process never registers with the SCM's start protocol, so it never gets a chance to report
// "running" — it just looks like an immediate, unexplained failure from the SCM's point of view, even
// though the app itself starts up fine when run directly from a console.
builder.Host.UseWindowsService(options => options.ServiceName = "FHIRBridge.LicenseServer");

// UseWindowsService() also auto-registers a Windows Event Log logging provider when actually running
// as a service. Confirmed on the real deployment VM: the LocalSystem service account isn't allowed to
// auto-create a brand-new event source on first write ("Cannot open log for source ... Access is
// denied" — Win32Exception 5), which crashes the WHOLE process the instant anything logs anything,
// before the host ever finishes starting. Silencing just this one provider's actual writes, rather than
// removing/fighting its registration, sidesteps the permission issue entirely without touching provider
// ordering — the rolling file sink registered above is what actually covers this app's need for
// persistent, operator-visible output once deployed as a service (there is no attached console there).
builder.Services.Configure<EventLogSettings>(settings => settings.Filter = (_, _) => false);

builder.Services.Configure<LicenseSigningOptions>(builder.Configuration.GetSection(LicenseSigningOptions.SectionName));
builder.Services.Configure<AdminCredentialsOptions>(builder.Configuration.GetSection(AdminCredentialsOptions.SectionName));

builder.Services.AddSingleton<LicenseSigningKeyProvider>();
builder.Services.AddSingleton<AdminAccountProvider>();
builder.Services.AddSingleton<LicenseTokenMinter>();
builder.Services.AddSingleton<LicenseTokenValidator>();

var connectionString = builder.Configuration.GetConnectionString("LicenseServerDb")
    ?? "Host=localhost;Port=5432;Database=FHIRBridgeLicenseServer;Username=postgres;Password=r00t1Pa$$2026;";
builder.Services.AddDbContext<LicenseServerDbContext>(options => options.UseNpgsql(connectionString));

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Account/Login";
        options.AccessDeniedPath = "/Account/Login";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.Cookie.Name = "FHIRBridgeLicenseServer.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
    });

builder.Services.AddAuthorization(options =>
{
    // Everything requires an authenticated admin session by default. /api/checkin, /api/license-requests,
    // and the login page are the three exceptions, each opted out explicitly (via AllowAnonymous) rather
    // than by narrowing this fallback policy — see CheckInEndpoints, LicenseRequestEndpoints, and
    // Pages/Account/Login.cshtml.cs. The intake endpoint upserts by UniqueKey rather than inserting
    // unboundedly, so this anonymous surface is bounded and deliberate, not an oversight.
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

builder.Services.AddRazorPages(options =>
{
    options.Conventions.AllowAnonymousToPage("/Account/Login");
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    // Self-sufficient on ANY connection string (a client's Azure Postgres, a fresh CI container, a
    // different local instance, anything) - no dependency on the local dev restart script, psql.exe, or
    // any other out-of-process tooling having run first. Database.MigrateAsync() below can create tables
    // inside an existing database but not the database itself, so that's handled first.
    await PostgresDatabaseInitializer.EnsureDatabaseExistsAsync(connectionString, logger);

    var db = scope.ServiceProvider.GetRequiredService<LicenseServerDbContext>();
    await db.Database.MigrateAsync();

    // Force both singletons to initialize now (rather than lazily on first request) so their startup log
    // lines (dev-key warning, generated admin password) are impossible to miss.
    scope.ServiceProvider.GetRequiredService<LicenseSigningKeyProvider>();
    scope.ServiceProvider.GetRequiredService<AdminAccountProvider>();

    // Same dev-placeholder warning as LicenseSigningKeyProvider above, for the OTHER shared secret this
    // server holds — see LicenseRequestSharedKey's remarks for what it does and does not protect.
    if (FHIRBridge.LicenseServer.Licensing.LicenseRequestSharedKey.IsUsingDevPlaceholder)
    {
        logger.LogWarning(
            "License request shared key: using the EMBEDDED DEV-ONLY placeholder (FHIRBRIDGE_LICENSE_REQUEST_SHARED_KEY " +
            "is not set). Set that env var to a real, non-committed 32-byte base64 secret — matching the value each " +
            "customer install sets on their own side — for anything beyond local development.");
    }
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages();
app.MapCheckInApi();
app.MapLicenseRequestApi();

app.Run();
