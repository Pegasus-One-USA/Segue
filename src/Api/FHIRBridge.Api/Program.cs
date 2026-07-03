using System.Text.Json.Serialization;
using FHIRBridge.Api.Workflows;
using FHIRBridge.Api.Security;
using FHIRBridge.Application;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Security;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Infrastructure;
using FHIRBridge.Infrastructure.Persistence;
using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Infrastructure.Workflows;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);

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
// Data Protection backs the encrypted OAuth launch-context and state tokens (ILaunchTokenProtector). In production,
// persist the key ring to shared storage (Key Vault / blob) so tokens survive restarts and work across instances.
builder.Services.AddDataProtection();
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
builder.Services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
builder.Services
    .AddFHIRBridgeApplication()
    .AddFHIRBridgeInfrastructure(builder.Configuration)
    .AddWorkflowCore()
    .AddWorkflowInfrastructure();

builder.Services.AddFhirBridgeAuthentication(builder.Configuration, builder.Environment);
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(AuthorizationPolicies.UnifiedAdmin, policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.AddRequirements(new UnifiedAdminRequirement());
    });

    // Permission-based policies — one per permission code declared on UnifiedPermissions
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

        policy
            .WithOrigins(origins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

var app = builder.Build();

// A [StandardPermission("some.code")] whose code doesn't match a UnifiedPermissions
// constant still gets a policy (see PermissionCatalog.AllPermissionCodes above), but
// it's almost always a typo — surface it at startup instead of a silent 403 later.
foreach (var undeclaredCode in PermissionCatalog.FindUndeclaredCodes(typeof(Program).Assembly))
{
    app.Logger.LogWarning(
        "Permission code '{PermissionCode}' is used via [StandardPermission] but is not declared on UnifiedPermissions.",
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

        var (status, message) = MapException(feature.Error);
        context.Response.StatusCode  = status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new { error = message });
    });
});

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

BootstrapDatabase(app);
SyncDiscoveredPermissions(app);

app.UseCors("Portal");
app.UseAuthentication();
app.Use(async (context, next) =>
{
    var requiresPasswordChange = context.User.Identity?.IsAuthenticated == true &&
                                 string.Equals(
                                     context.User.FindFirst("pwd_change_required")?.Value,
                                     "true",
                                     StringComparison.OrdinalIgnoreCase);

    if (requiresPasswordChange &&
        context.Request.Path.StartsWithSegments("/api/v1") &&
        !context.Request.Path.StartsWithSegments("/api/v1/auth/internal/change-password") &&
        !context.Request.Path.StartsWithSegments("/api/v1/auth/me"))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new
        {
            message = "Password change is required before using FHIRBridge."
        });
        return;
    }

    await next();
});
app.UseAuthorization();
app.MapControllers();
app.MapWorkflowEndpoints();

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

static async Task SyncDiscoveredPermissionsAsync(IUserAccessRepository repository, ILogger logger)
{
    var discoveredCodes = PermissionCatalog.DiscoveredCodes(typeof(Program).Assembly);
    var existingPermissions = await repository.GetPermissionsAsync(CancellationToken.None);
    var existingCodes = new HashSet<string>(existingPermissions.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);

    var superAdminRole = await repository.GetRoleByNameAsync(UnifiedRoles.SuperAdmin, CancellationToken.None);

    foreach (var code in discoveredCodes.Where(code => !existingCodes.Contains(code)))
    {
        var permission = new Permission(
            Guid.NewGuid(),
            code,
            $"Auto-registered permission for '{code}'.",
            CategoryFromPermissionCode(code),
            isSystem: false);

        await repository.AddPermissionAsync(permission, CancellationToken.None);
        logger.LogWarning(
            "Auto-registered new permission '{PermissionCode}' discovered via [StandardPermission]; review its category/description in the Permissions table.",
            code);

        if (superAdminRole is not null)
        {
            await repository.AddRolePermissionAsync(superAdminRole.Id, permission.Id, CancellationToken.None);
        }
    }
}

static string CategoryFromPermissionCode(string code)
{
    var prefix = code.Split('.', 2)[0];

    return prefix.Length == 0 ? prefix : char.ToUpperInvariant(prefix[0]) + prefix[1..];
}

static (int status, string message) MapException(Exception ex)
{
    if (ex is not InvalidOperationException and not UnauthorizedAccessException
                                             and not ArgumentException)
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
