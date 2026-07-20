using FHIRBridge.Observability.Logging;
using Microsoft.Extensions.FileProviders;
using Serilog;
using Yarp.ReverseProxy.Configuration;

var builder = WebApplication.CreateBuilder(args);

// No-ops unless actually launched by that OS's service manager — lets the same published
// output run as a systemd service on Linux or a Windows Service, with `dotnet run` unaffected.
builder.Host.UseWindowsService().UseSystemd();

builder.Host.UseSerilog((context, loggerConfig) =>
    loggerConfig.ConfigureFhirBridge(context.Configuration, "FHIRBridge.Gateway"));

// Routes are fixed (this Gateway only ever proxies these two path patterns to the one Api it
// fronts) — only the destination address varies per deployment, so that's the one thing pulled
// from config, as a single flat setting instead of hand-authoring YARP's full Routes/Clusters
// schema per environment. Defaults to production's own address, so neither local dev nor
// production needs to set this explicitly; other environments override just this one key.
var apiBaseUrl = builder.Configuration["ApiBaseUrl"] ?? "http://127.0.0.1:5000/";
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

// Serve the portal's production build for everything that isn't under /api — YARP (mapped
// below) owns /api/**, so this static-file branch owns the rest of the path space.
// PhysicalFileProvider requires an absolute path; resolve relative config values (used for
// local dev) against the working directory the same way Path.GetFullPath always would.
var configuredRoot = builder.Configuration["StaticFiles:RootPath"];
var staticRoot = string.IsNullOrWhiteSpace(configuredRoot) ? null : Path.GetFullPath(configuredRoot);
if (staticRoot is not null && Directory.Exists(staticRoot))
{
    var fileProvider = new PhysicalFileProvider(staticRoot);

    app.UseWhen(
        context => !context.Request.Path.StartsWithSegments("/api"),
        branch =>
        {
            branch.UseDefaultFiles(new DefaultFilesOptions { FileProvider = fileProvider });
            branch.UseStaticFiles(new StaticFileOptions { FileProvider = fileProvider });

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
