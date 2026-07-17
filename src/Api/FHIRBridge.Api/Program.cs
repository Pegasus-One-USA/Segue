using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using FHIRBridge.Api.Workflows;
using FHIRBridge.Api.Security;
using FHIRBridge.Observability.Logging;
using Microsoft.AspNetCore.DataProtection;
using FHIRBridge.Application;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Abstractions.Sources;
using FHIRBridge.Application.Security;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Infrastructure;
using FHIRBridge.Infrastructure.Persistence;
using FHIRBridge.Infrastructure.Persistence.Workflows;
using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Infrastructure.Workflows;
using FHIRBridge.SharedKernel.Exceptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// No-op unless the process is actually started by the Windows Service Control Manager (e.g. `dotnet run`
// and console execution are unaffected) — lets the same published output run standalone or as a service.
builder.Host.UseWindowsService(options => options.ServiceName = "FHIRBridge.Api");

builder.Host.UseSerilog((context, loggerConfig) =>
    loggerConfig.ConfigureFhirBridge(context.Configuration, "FHIRBridge.Api"));

if (builder.Environment.IsDevelopment())
{
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Authentication:SigningKey"] = builder.Configuration["Authentication:SigningKey"]
            ?? "StepBase-FHIRBridge-local-development-signing-key-2026-06-22"
    });
}

builder.Services
    .AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });
// Minimal-API endpoints (WorkflowEndpoints) use a separate JSON options bag from MVC. Register the same string-enum
// converter so the workflow/build DTOs accept enum names (e.g. "Sample", "SqlServer") like the MVC controllers do;
// numeric enum values still deserialize, so the existing category-as-int graph serializer keeps working.
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
// Data Protection backs the encrypted OAuth launch-context and state tokens (ILaunchTokenProtector).
// In production the key ring MUST be persisted to shared storage so tokens survive restarts and work
// across instances (otherwise each node/restart mints a new key and can't decrypt the others' tokens).
// Set DataProtection:KeyRingPath to a shared, backed-up volume (Azure Files, K8s PVC, etc.). Without a
// path we keep the default (machine-local) ring, which is fine only for single-instance dev.
var dataProtection = builder.Services.AddDataProtection()
    .SetApplicationName(builder.Configuration["DataProtection:ApplicationName"] ?? "FHIRBridge");

var keyRingPath = builder.Configuration["DataProtection:KeyRingPath"];
if (!string.IsNullOrWhiteSpace(keyRingPath))
{
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keyRingPath));
}
else if (!builder.Environment.IsDevelopment())
{
    // Surface the misconfiguration loudly instead of silently issuing un-shareable keys in production.
    Console.Error.WriteLine(
        "[WARN] DataProtection:KeyRingPath is not set. In a multi-instance deployment, OAuth/launch " +
        "tokens will not be decryptable across instances or restarts. Configure a shared key-ring path.");
}
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "FHIRBridge API",
        Version = "v1",
        Description = "REST API for FHIRBridge — FHIR data integration and transformation platform."
    });

    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Enter a JWT bearer token issued by local login or Microsoft Entra ID."
    });

    options.AddSecurityRequirement(openApiDocument => new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecuritySchemeReference("Bearer", openApiDocument, null),
            []
        }
    });
});
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUserService, HttpContextCurrentUserService>();
builder.Services.AddScoped<IAccessTokenIssuer, JwtAccessTokenIssuer>();
builder.Services.AddScoped<IAuthorizationHandler, UnifiedAdminAuthorizationHandler>();
builder.Services.AddScoped<IAuthorizationHandler, SuperAdminOnlyAuthorizationHandler>();
builder.Services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
builder.Services
    .AddFHIRBridgeApplication()
    .AddPatientStandaloneApplicationServices()
    .AddFHIRBridgeInfrastructure(builder.Configuration)
    .AddWorkflowCore()
    .AddWorkflowInfrastructure();

// Scenario A: back the graph engine's stores with SQL (must follow AddWorkflowCore to win the registration).
// Scenario B: also wires the launch-graph projection/resolver + feature flag (default OFF). Gated the same way
// AddFHIRBridgeInfrastructure gates its own EF registrations: no connection string (e.g. the integration-test
// host) means FHIRBridgeDbContext itself is never registered, so wiring SQL-backed stores here would leave
// IWorkflowDefinitionStore unresolvable the moment anything actually depends on it. AddWorkflowCore()'s
// InMemoryWorkflowDefinitionStore stays the registration in that case.
if (!string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("FHIRBridgeDb")))
{
    builder.Services.AddWorkflowSqlPersistence(builder.Configuration);
}

builder.Services.AddFhirBridgeAuthentication(builder.Configuration, builder.Environment);
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(AuthorizationPolicies.UnifiedAdmin, policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.AddRequirements(new UnifiedAdminRequirement());
    });

    options.AddPolicy(AuthorizationPolicies.SuperAdminOnly, policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.AddRequirements(new SuperAdminOnlyRequirement());
    });

    // Permission-based policies — one per permission code declared in RbacSeedData.Permissions
    // or referenced via [StandardPermission] on a controller (see PermissionCatalog).
    foreach (var code in PermissionCatalog.AllPermissionCodes(typeof(Program).Assembly))
    {
        options.AddPolicy(
            AuthorizationPolicies.HasPermission(code),
            policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.AddRequirements(new PermissionRequirement(code));
            });
    }
});
builder.Services.AddCors(options =>
{
    options.AddPolicy("Portal", policy =>
    {
        var origins = builder.Configuration
            .GetSection("Portal:AllowedOrigins")
            .Get<string[]>() ?? ["http://localhost:4200", "https://localhost:4200"];

        // Narrowed from AllowAnyHeader/AllowAnyMethod (HIPAA/SOC2 CC6.1): a credentialed
        // CORS policy should expose only the verbs and headers the portal actually uses.
        policy
            .WithOrigins(origins)
            .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS")
            .WithHeaders("Authorization", "Content-Type", "Accept", "X-Correlation-Id")
            .AllowCredentials();
    });
});

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor |
        ForwardedHeaders.XForwardedProto |
        ForwardedHeaders.XForwardedHost;
    options.ForwardLimit = builder.Configuration.GetValue<int?>("ForwardedHeaders:ForwardLimit") ?? 2;

    if (builder.Configuration.GetValue("ForwardedHeaders:TrustAllProxies", false))
    {
        options.KnownNetworks.Clear();
        options.KnownProxies.Clear();
    }
});

// Rate limiting (HIPAA/SOC2 CC6.2): throttle unauthenticated credential + ingestion endpoints
// to blunt brute-force and abuse. Partitioned per client IP; sensitive endpoints opt in via
// [EnableRateLimiting("auth")] / ("oauth") / ("webhook"). Limits are configurable under "RateLimiting:*".
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    var authPermit = builder.Configuration.GetValue<int?>("RateLimiting:Auth:PermitPerWindow") ?? 10;
    var authWindowMinutes = builder.Configuration.GetValue<int?>("RateLimiting:Auth:WindowMinutes") ?? 5;
    var oauthPermit = builder.Configuration.GetValue<int?>("RateLimiting:OAuth:PermitPerWindow") ?? 30;
    var oauthWindowMinutes = builder.Configuration.GetValue<int?>("RateLimiting:OAuth:WindowMinutes") ?? 5;
    var webhookPermit = builder.Configuration.GetValue<int?>("RateLimiting:Webhook:PermitPerWindow") ?? 120;
    var webhookWindowMinutes = builder.Configuration.GetValue<int?>("RateLimiting:Webhook:WindowMinutes") ?? 1;

    options.AddPolicy("auth", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            ClientPartitionKey(context),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = authPermit,
                Window = TimeSpan.FromMinutes(authWindowMinutes),
                QueueLimit = 0
            }));

    options.AddPolicy("oauth", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            ClientPartitionKey(context),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = oauthPermit,
                Window = TimeSpan.FromMinutes(oauthWindowMinutes),
                QueueLimit = 0
            }));

    options.AddPolicy("webhook", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            ClientPartitionKey(context),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = webhookPermit,
                Window = TimeSpan.FromMinutes(webhookWindowMinutes),
                QueueLimit = 0
            }));

    static string ClientPartitionKey(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
});

var app = builder.Build();

app.UseForwardedHeaders();

// A [StandardPermission(group, action)] whose derived code has no matching
// RbacSeedData.Permissions entry still gets a policy (see PermissionCatalog.AllPermissionCodes
// above), but it's almost always a sign the seed data is missing that permission — surface it
// at startup instead of a silent 403 later.
foreach (var undeclaredCode in PermissionCatalog.FindUndeclaredCodes(typeof(Program).Assembly))
{
    app.Logger.LogWarning(
        "Permission code '{PermissionCode}' is used via [StandardPermission] but is not declared in RbacSeedData.Permissions.",
        undeclaredCode);
}

// ── Global exception handler ─────────────────────────────────────────────────
// Maps domain InvalidOperationException to appropriate HTTP status codes so the
// API never leaks raw 500s for expected business-rule / validation failures.
app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        var feature = context.Features
            .Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
        if (feature?.Error is null) return;

        app.Logger.LogError(feature.Error, "Unhandled exception on {Path}.", context.Request.Path);

        var (status, message) = MapException(feature.Error);
        context.Response.StatusCode  = status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new { error = message });
    });
});

// Security response headers (HIPAA/SOC2 CC6.1): defense-in-depth on every response.
// Temporary: Swagger:Enabled lets ops turn Swagger on in Production without a redeploy (and back
// off again the same way) while the team still needs it there. Remove once no longer needed.
var swaggerEnabled = app.Environment.IsDevelopment() || app.Configuration.GetValue("Swagger:Enabled", false);

// The strict Content-Security-Policy is skipped for Swagger's own path when Swagger is enabled —
// Swagger UI needs inline scripts/styles that 'default-src none' would otherwise block.
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "no-referrer";
    headers["X-Permitted-Cross-Domain-Policies"] = "none";
    if (!app.Environment.IsDevelopment() && !(swaggerEnabled && context.Request.Path.StartsWithSegments("/swagger")))
    {
        // /api responses carry no renderable content, so lock them down completely. Everything else is the
        // portal's static build (see wwwroot, served below) — it needs 'self' to load its own JS/CSS/fonts,
        // where 'none' would blank-page the SPA.
        headers["Content-Security-Policy"] = context.Request.Path.StartsWithSegments("/api")
            ? "default-src 'none'; frame-ancestors 'none'"
            : "default-src 'self'; frame-ancestors 'none'; base-uri 'self'";
    }

    await next();
});

if (!app.Environment.IsDevelopment())
{
    // Enforce HTTPS at the application layer instead of relying solely on an upstream proxy.
    app.UseHsts();
    app.UseHttpsRedirection();
}

if (swaggerEnabled)
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

BootstrapDatabase(app);
SyncDiscoveredPermissions(app);

// Serves the Angular portal's production build when it's been copied into wwwroot (see deploy/windows) —
// a no-op in local dev, where wwwroot doesn't exist and the portal runs separately via `ng serve`.
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapHealthChecks("/health");

app.UseCors("Portal");
app.UseAuthentication();

// Each entry blocks every /api/v1 route except its own allowlist while its claim is "true" — e.g.
// must change password, or must finish MFA enrollment. One shared check so a third gate is just
// another entry here, not a third copy-pasted middleware block.
var sessionGates = new[]
{
    (Claim: "pwd_change_required",
     Allowed: new[] { "/api/v1/auth/internal/change-password", "/api/v1/auth/me" },
     Message: "Password change is required before using FHIRBridge."),
    (Claim: "mfa_setup_required",
     Allowed: new[] { "/api/v1/auth/mfa", "/api/v1/auth/me" },
     Message: "Two-factor authentication setup is required before using FHIRBridge."),
};

app.Use(async (context, next) =>
{
    foreach (var gate in sessionGates)
    {
        var required = context.User.Identity?.IsAuthenticated == true &&
                        string.Equals(context.User.FindFirst(gate.Claim)?.Value, "true", StringComparison.OrdinalIgnoreCase);

        if (required &&
            context.Request.Path.StartsWithSegments("/api/v1") &&
            !gate.Allowed.Any(allowed => context.Request.Path.StartsWithSegments(allowed)))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { message = gate.Message });
            return;
        }
    }

    await next();
});
app.UseAuthorization();
// Enabled by default; can be turned off for hermetic tests or single-tenant deployments that
// throttle upstream. Policies are always registered so [EnableRateLimiting] metadata resolves.
if (app.Configuration.GetValue("RateLimiting:Enabled", true))
{
    app.UseRateLimiter();
}
app.MapControllers();
app.MapWorkflowEndpoints();

// Client-side (Angular) routes have no server-side match — fall back to index.html so deep links
// and refreshes on e.g. /workflows/123 resolve instead of 404ing. No-ops if wwwroot/index.html
// isn't present (local dev, portal running separately).
app.MapFallbackToFile("index.html");

app.Run();

// Applies pending EF migrations (empty DB → full schema) then runs the RBAC bootstrapper so the
// permission catalog + system roles + grants self-provision on boot. No users are created — a freshly
// migrated database has zero users, so the portal routes to first-run setup (POST /auth/setup-superadmin).
// No-ops on the in-memory path (no DbContext / no bootstrapper registered); that path self-seeds the same
// RBAC catalog in InMemoryUserAccessRepository's constructor.
static void BootstrapDatabase(WebApplication app)
{
    using var scope = app.Services.CreateScope();

    var dbContext = scope.ServiceProvider.GetService<FHIRBridgeDbContext>();
    if (dbContext is not null)
    {
        dbContext.Database.Migrate();
    }

    var bootstrapper = scope.ServiceProvider.GetService<IRbacBootstrapper>();
    bootstrapper?.EnsureAsync(CancellationToken.None).GetAwaiter().GetResult();

    // Runs every registered vendor endpoint-directory seeder (currently just Epic's) — adding a new vendor never
    // touches this call site, only DependencyInjection.cs's registration list.
    foreach (var seeder in scope.ServiceProvider.GetServices<IEhrEndpointDirectorySeeder>())
    {
        seeder.EnsureAsync(CancellationToken.None).GetAwaiter().GetResult();
    }
}

// Reflection discovers every [StandardPermission] code in use (see PermissionCatalog), but only
// registering an in-memory authorization policy for it isn't enough to let anyone through — the
// code also has to exist as a Permission row before any role can be granted it. This closes that
// gap automatically at startup instead of requiring a manual PermissionConfiguration + migration
// edit for every new permission-gated feature.
static void SyncDiscoveredPermissions(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var repository = scope.ServiceProvider.GetService<IUserAccessRepository>();
    if (repository is null)
    {
        return;
    }

    SyncDiscoveredPermissionsAsync(repository, app.Logger).GetAwaiter().GetResult();
}

static async Task SyncDiscoveredPermissionsAsync(
    IUserAccessRepository repository,
    Microsoft.Extensions.Logging.ILogger logger)
{
    // Every permission referenced by a [StandardPermission] attribute. PermissionCatalog already
    // deduplicates these by code and combines descriptions/instances, so each entry here is unique
    // by Id — no further grouping needed.
    var discoveredPermissions = PermissionCatalog.DiscoveredPermissions(typeof(Program).Assembly);
    var discoveredPermissionsById = discoveredPermissions.ToDictionary(p => p.Id);

    var existingPermissions = await repository.GetPermissionsAsync(CancellationToken.None);
    var existingPermissionsById = existingPermissions.ToDictionary(p => p.Id);

    // Permissions declared in RbacSeedData are owned by RbacBootstrapper and must never be
    // deactivated by this method.
    var seedDeclaredPermissionIds = new HashSet<Guid>(RbacSeedData.Permissions.Select(p => p.Id));

    var superAdminRole = await repository.GetRoleByNameAsync(UnifiedRoles.SuperAdmin, CancellationToken.None);

    // 1. Deactivate a non-seeded permission that's active but no longer discovered in code.
    foreach (var existingPermission in existingPermissions)
    {
        if (!existingPermission.IsActive)
        {
            continue;
        }

        if (seedDeclaredPermissionIds.Contains(existingPermission.Id))
        {
            continue;
        }

        if (!discoveredPermissionsById.ContainsKey(existingPermission.Id))
        {
            existingPermission.Deactivate();
            await repository.UpdatePermissionAsync(existingPermission, CancellationToken.None);
        }
    }

    // 2 & 3. Update an existing permission's fields, or create a new one.
    foreach (var discoveredPermission in discoveredPermissions)
    {
        var displayName = PermissionTaxonomy.BuildPermissionDisplayName(
            discoveredPermission.Group,
            discoveredPermission.Action);

        var description = string.IsNullOrWhiteSpace(discoveredPermission.Description)
            ? $"Auto-registered permission for '{discoveredPermission.Code}'."
            : discoveredPermission.Description;

        if (existingPermissionsById.TryGetValue(discoveredPermission.Id, out var existingPermission))
        {
            // Keep every mutable field synchronized with what's currently discovered from source code.
            var changed = false;

            if (existingPermission.Name != discoveredPermission.Code)
            {
                existingPermission.UpdateName(discoveredPermission.Code);
                changed = true;
            }

            if (existingPermission.DisplayName != displayName)
            {
                existingPermission.UpdateDisplayName(displayName);
                changed = true;
            }

            if (!string.Equals(existingPermission.Description, description, StringComparison.Ordinal))
            {
                existingPermission.UpdateDescription(description);
                changed = true;
            }

            if (existingPermission.Instances != discoveredPermission.Instances)
            {
                existingPermission.UpdateInstances(discoveredPermission.Instances);
                changed = true;
            }

            if (!existingPermission.IsActive)
            {
                existingPermission.Activate();
                changed = true;
            }

            if (changed)
            {
                await repository.UpdatePermissionAsync(existingPermission, CancellationToken.None);
            }

            continue;
        }

        // New permission.
        var groupId = RbacSeedData.GroupIdsByCode[discoveredPermission.Group];

        var newPermission = new Permission(
            discoveredPermission.Id,
            discoveredPermission.Code,
            displayName,
            description,
            groupId,
            isSystem: false,
            instances: discoveredPermission.Instances);

        await repository.AddPermissionAsync(newPermission, CancellationToken.None);

        if (string.IsNullOrWhiteSpace(discoveredPermission.Description))
        {
            logger.LogWarning(
                "Auto-registered new permission '{PermissionCode}' discovered via [StandardPermission] with no description; add one to the attribute.",
                discoveredPermission.Code);
        }

        if (superAdminRole is not null)
        {
            await repository.AddRolePermissionAsync(superAdminRole.Id, newPermission.Id, CancellationToken.None);
        }
    }
}

static (int status, string message) MapException(Exception ex)
{
    // NotFoundException (and other FHIRBridgeException subtypes) are expected domain-level failures — e.g. a
    // launch/checkpoint URL whose referenced WorkflowDefinition/SourceConnection/Route no longer exists — and must
    // reach the message-based classification below rather than falling into the generic 500 bucket.
    if (ex is NotFoundException)
        return (StatusCodes.Status404NotFound, ex.Message);

    if (ex is not InvalidOperationException and not UnauthorizedAccessException
                                             and not ArgumentException
                                             and not FHIRBridgeException)
        return (StatusCodes.Status500InternalServerError, "An unexpected error occurred.");

    if (ex is UnauthorizedAccessException || ex is ArgumentException a && a.Message.Contains("unauthorized"))
        return (StatusCodes.Status401Unauthorized, ex.Message);

    var msg = ex.Message;

    // 404 – resource not found
    if (msg.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
        msg.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
        return (StatusCodes.Status404NotFound, msg);

    // 409 – resource conflict
    if (msg.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        return (StatusCodes.Status409Conflict, msg);

    // 401 – authentication / token failures
    if (msg.Contains("invalid or expired", StringComparison.OrdinalIgnoreCase) ||
        msg.Contains("email or password", StringComparison.OrdinalIgnoreCase) ||
        msg.Contains("Current password is invalid", StringComparison.OrdinalIgnoreCase))
        return (StatusCodes.Status401Unauthorized, msg);

    // 400 – all other domain / validation errors
    return (StatusCodes.Status400BadRequest, msg);
}
