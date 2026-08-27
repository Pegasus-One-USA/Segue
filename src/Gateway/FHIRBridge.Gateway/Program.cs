using FHIRBridge.Observability.Logging;
using Microsoft.Extensions.FileProviders;
using Serilog;
using Yarp.ReverseProxy.Configuration;

var builder = WebApplication.CreateBuilder(args);

// Every non-dev deployment MUST set ASPNETCORE_URLS explicitly (the Windows Service's registry
// Environment value — see deploy/windows/Deploy-FHIRBridge*.ps1). Kestrel's own built-in fallback
// (http://localhost:5000) is a shared, unconfigurable port; silently landing on it risks colliding
// with another environment's service, or an unrelated application entirely, on the same host.
if (!builder.Environment.IsDevelopment() && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
{
    throw new InvalidOperationException(
        "ASPNETCORE_URLS is not set for this environment. Refusing to fall back to Kestrel's default port — " +
        "set it explicitly via this Windows Service's registry Environment value (deploy/windows/Deploy-FHIRBridge*.ps1).");
}

// No-ops unless actually launched by that OS's service manager — lets the same published
// output run as a systemd service on Linux or a Windows Service, with `dotnet run` unaffected.
builder.Host.UseWindowsService().UseSystemd();

builder.Host.UseSerilog((context, loggerConfig) =>
    loggerConfig.ConfigureFhirBridge(context.Configuration, "FHIRBridge.Gateway"));

// Routes are fixed (this Gateway only ever proxies these two path patterns to the one Api it
// fronts) — only the destination address varies per deployment, so that's the one thing pulled
// from config, as a single flat setting. Every environment (including production) must set this
// explicitly in its own appsettings.Production.json overlay — there is no shared fallback here:
// silently defaulting to one environment's Api address risks a misconfigured environment proxying
// its traffic straight into another one's (e.g. production's).
var apiBaseUrl = builder.Configuration["ApiBaseUrl"];
if (string.IsNullOrWhiteSpace(apiBaseUrl) && !builder.Environment.IsDevelopment())
{
    throw new InvalidOperationException(
        "ApiBaseUrl is not set for this environment. Refusing to fall back to another environment's Api address — " +
        "set it explicitly in this environment's appsettings.Production.json overlay.");
}
apiBaseUrl ??= "http://127.0.0.1:5000/"; // local dev only — matches FHIRBridge.Api's launchSettings.json port
builder.Services.AddReverseProxy().LoadFromMemory(
    routes:
    [
        new RouteConfig
        {
            RouteId = "api-route",
            ClusterId = "api-cluster",
            Match = new RouteMatch { Path = "/api/{**catch-all}" }
        },
        new RouteConfig
        {
            RouteId = "swagger-route",
            ClusterId = "api-cluster",
            Match = new RouteMatch { Path = "/swagger/{**catch-all}" }
        }
    ],
    clusters:
    [
        new ClusterConfig
        {
            ClusterId = "api-cluster",
            Destinations = new Dictionary<string, DestinationConfig>
            {
                ["destination1"] = new DestinationConfig { Address = apiBaseUrl }
            }
        }
    ]);

var app = builder.Build();

// Serve the portal's production build for everything that isn't proxied to the Api (/api/** and
// /swagger/**, both mapped below) — this static-file branch owns the rest of the path space.
// PhysicalFileProvider requires an absolute path; resolve relative config values (used for
// local dev) against the working directory the same way Path.GetFullPath always would.
var configuredRoot = builder.Configuration["StaticFiles:RootPath"];
var staticRoot = string.IsNullOrWhiteSpace(configuredRoot) ? null : Path.GetFullPath(configuredRoot);
if (staticRoot is not null && Directory.Exists(staticRoot))
{
    var fileProvider = new PhysicalFileProvider(staticRoot);

    app.UseWhen(
        context => !context.Request.Path.StartsWithSegments("/api") && !context.Request.Path.StartsWithSegments("/swagger"),
        branch =>
        {
            branch.UseDefaultFiles(new DefaultFilesOptions { FileProvider = fileProvider });
            // ServeUnknownFileTypes: ACME HTTP-01 challenge tokens (under .well-known/acme-challenge/)
            // have no file extension, so the default FileExtensionContentTypeProvider would 404 them —
            // needed so Gateway can serve certbot's --webroot challenge files without taking the site down.
            branch.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = fileProvider,
                ServeUnknownFileTypes = true,
                DefaultContentType = "application/octet-stream"
            });

            // SPA fallback: unmatched paths (Angular client-side routes) resolve to index.html
            // rather than 404ing, so deep links and refreshes work.
            branch.Run(async context =>
            {
                context.Response.ContentType = "text/html";
                await using var stream = fileProvider.GetFileInfo("index.html").CreateReadStream();
                await stream.CopyToAsync(context.Response.Body);
            });
        });
}
else if (!builder.Environment.IsDevelopment())
{
    Console.Error.WriteLine(
        "[WARN] StaticFiles:RootPath is not set or does not exist. The portal UI will not be served — only /api/** will respond.");
}

app.MapReverseProxy();

app.Run();
