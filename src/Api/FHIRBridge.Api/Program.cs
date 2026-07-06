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

        policy
            .WithOrigins(origins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

var app = builder.Build();

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

static async Task SyncDiscoveredPermissionsAsync_Old(IUserAccessRepository repository, ILogger logger)
{
    var discoveredPermissions = PermissionCatalog.DiscoveredPermissions(typeof(Program).Assembly);
    var existingPermissions = await repository.GetPermissionsAsync(CancellationToken.None);

    var existingById = existingPermissions.ToDictionary(p => p.Id);

    // Name is no longer a database-enforced unique key (Id, the primary key, already is the deterministic
    // encoding of Group+Action — see PermissionTaxonomy.BuildPermissionId), so this is built by indexer
    // assignment rather than ToDictionary: more than one active row sharing a Name would otherwise throw
    // here instead of just picking one. Only active rows are considered — a deactivated permission can
    // share a Name with a newer active one, and the active row is always the real one to check
    // "is this Name still taken" against below.
    var existingByName = new Dictionary<Guid, Permission>();
    foreach (var permission in existingPermissions)
    {
        if (permission.IsActive)
        {
            existingByName[permission.Id] = permission;
        }
    }

    // A permission declared in RbacSeedData.Permissions is owned end-to-end by RbacBootstrapper, including
    // self-healing its Id if the derivation formula ever changes — this method only manages permissions
    // that exist solely because a [StandardPermission] attribute references them.
    var declaredIds = new HashSet<Guid>(RbacSeedData.Permissions.Select(p => p.Id));

    var superAdminRole = await repository.GetRoleByNameAsync(UnifiedRoles.SuperAdmin, CancellationToken.None);

    var discoveredIds = new HashSet<Guid>();

    foreach (var discovered in discoveredPermissions)
    {
        var permissionId = PermissionTaxonomy.BuildPermissionId(discovered.Group, discovered.Action);

        //if (declaredIds.Contains(permissionId))
        //{
        //    continue;
        //}

        discoveredIds.Add(permissionId);

        if (existingById.TryGetValue(permissionId, out var existing))
        {
            // Instances/description can change as attributes are added, removed, or reworded on
            // controllers over time — keep both current on every boot instead of only ever setting them
            // once at creation.
            var changed = false;

            if (existing.Instances != discovered.Instances)
            {
                existing.UpdateInstances(discovered.Instances);
                changed = true;
            }

            if (discovered.Description is not null
                && !existing.Description.Contains(discovered.Description, StringComparison.OrdinalIgnoreCase))
            {
                existing.UpdateDescription($"{existing.Description} | {discovered.Description}");
                changed = true;
            }

            if (!existing.IsActive)
            {
                existing.Activate();
                changed = true;
            }

            if (changed)
            {
                await repository.UpdatePermissionAsync(existing, CancellationToken.None);
            }

            continue;
        }

        // No row has today's Id for this permission. An active row may still occupy the same Name under an
        // older Id (e.g. the Id-derivation formula changed since this permission was first discovered) —
        // deactivate that one so it's clear which single row is the current, active permission for this
        // Name; the new row below is free to take the same Name since Name is no longer a unique key.
        if (existingByName.TryGetValue(permissionId, out var stale))
        {
            stale.Deactivate();
            await repository.UpdatePermissionAsync(stale, CancellationToken.None);
        }

        // Every PermissionGroupCode member always has a RbacSeedData.Groups entry (see
        // PermissionTaxonomyCompletenessTests), so this lookup can never fail.
        var groupId = RbacSeedData.GroupIdsByCode[discovered.Group];

        var permission = new Permission(
            permissionId,
            discovered.Code,
            PermissionTaxonomy.BuildPermissionDisplayName(discovered.Group, discovered.Action),
            discovered.Description ?? $"Auto-registered permission for '{discovered.Code}'.",
            groupId,
            isSystem: false,
            instances: discovered.Instances);

        await repository.AddPermissionAsync(permission, CancellationToken.None);

        if (discovered.Description is null)
        {
            logger.LogWarning(
                "Auto-registered new permission '{PermissionCode}' discovered via [StandardPermission] with no description; add one to the attribute.",
                discovered.Code);
        }

        if (superAdminRole is not null)
        {
            await repository.AddRolePermissionAsync(superAdminRole.Id, permission.Id, CancellationToken.None);
        }
    }

    // A discovered-only permission no longer referenced by any [StandardPermission] attribute was removed
    // from code — deactivate it rather than deleting it, so PermissionAllocations referencing it (role
    // grants, audit history) stay intact.
    foreach (var existing in existingPermissions)
    {
        if (!existing.IsSystem && existing.IsActive && !discoveredIds.Contains(existing.Id))
        {
            existing.Deactivate();
            await repository.UpdatePermissionAsync(existing, CancellationToken.None);
        }
    }
}

static async Task SyncDiscoveredPermissionsAsync(
    IUserAccessRepository repository,
    ILogger logger)
{
    // Discover every permission referenced by [StandardPermission] attributes.
    // The same permission can legitimately be discovered multiple times because
    // different controllers/actions may reference it with different descriptions.
    var discoveredPermissions =
        PermissionCatalog.DiscoveredPermissions(typeof(Program).Assembly);

    // Load every permission currently stored in the database.
    var existingPermissions =
        await repository.GetPermissionsAsync(CancellationToken.None);

    // Index existing permissions by their deterministic Id.
    var existingById = existingPermissions.ToDictionary(p => p.Id);

    // Group discovered permissions by Id so each permission is processed only once.
    //
    // For duplicate discoveries:
    //  - The first discovered record supplies all metadata (Code, Group, Action, Instances, etc.).
    //  - Every unique non-empty description is combined into a single string separated by " | ".
    var discoveredById = discoveredPermissions
        .GroupBy(p => PermissionTaxonomy.BuildPermissionId(p.Group, p.Action))
        .ToDictionary(
            g => g.Key,
            g =>
            {
                var first = g.First();

                return new
                {
                    Id = g.Key,
                    First = first,

                    Description = string.Join(
                        " | ",
                        g.Select(x => x.Description)
                         .Where(x => !string.IsNullOrWhiteSpace(x))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
                };
            });

    // Seeded permissions are owned by RbacSeedData and should not be
    // deactivated by this synchronization process.
    var declaredIds = new HashSet<Guid>(
        RbacSeedData.Permissions.Select(x => x.Id));

    var superAdminRole = await repository.GetRoleByNameAsync(
        UnifiedRoles.SuperAdmin,
        CancellationToken.None);

    //----------------------------------------------------------------------
    // 1. Deactivate permissions that no longer exist in code.
    //----------------------------------------------------------------------
    foreach (var existing in existingPermissions)
    {
        //if (existing.IsSystem)
        //{
        //    continue;
        //}

        if (!existing.IsActive)
        {
            continue;
        }

        if (declaredIds.Contains(existing.Id))
        {
            continue;
        }

        if (!discoveredById.ContainsKey(existing.Id))
        {
            existing.Deactivate();
            await repository.UpdatePermissionAsync(
                existing,
                CancellationToken.None);
        }
    }

    //----------------------------------------------------------------------
    // 2 & 3. Update existing permissions or create new ones.
    //----------------------------------------------------------------------
    foreach (var discovered in discoveredById.Values)
    {
        var first = discovered.First;

        var permissionId = discovered.Id;

        var displayName = PermissionTaxonomy.BuildPermissionDisplayName(
            first.Group,
            first.Action);

        var description = string.IsNullOrWhiteSpace(discovered.Description)
            ? $"Auto-registered permission for '{first.Code}'."
            : discovered.Description;

        if (existingById.TryGetValue(permissionId, out var existing))
        {
            // Existing permission found.
            // Keep every mutable field synchronized with what is currently
            // discovered from source code.

            var changed = false;

            if (existing.Name != first.Code)
            {
                existing.UpdateName(first.Code);
                changed = true;
            }

            if (existing.DisplayName != displayName)
            {
                existing.UpdateDisplayName(displayName);
                changed = true;
            }

            if (!string.Equals(
                    existing.Description,
                    description,
                    StringComparison.Ordinal))
            {
                existing.UpdateDescription(description);
                changed = true;
            }

            if (existing.Instances != first.Instances)
            {
                existing.UpdateInstances(first.Instances);
                changed = true;
            }

            if (!existing.IsActive)
            {
                existing.Activate();
                changed = true;
            }

            if (changed)
            {
                await repository.UpdatePermissionAsync(
                    existing,
                    CancellationToken.None);
            }

            continue;
        }

        //------------------------------------------------------------------
        // New permission.
        //------------------------------------------------------------------

        var groupId = RbacSeedData.GroupIdsByCode[first.Group];

        var permission = new Permission(
            permissionId,
            first.Code,
            displayName,
            description,
            groupId,
            isSystem: false,
            instances: first.Instances);

        await repository.AddPermissionAsync(
            permission,
            CancellationToken.None);

        if (string.IsNullOrWhiteSpace(discovered.Description))
        {
            logger.LogWarning(
                "Auto-registered new permission '{PermissionCode}' discovered via [StandardPermission] with no description; add one to the attribute.",
                first.Code);
        }

        if (superAdminRole is not null)
        {
            await repository.AddRolePermissionAsync(
                superAdminRole.Id,
                permission.Id,
                CancellationToken.None);
        }
    }
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
