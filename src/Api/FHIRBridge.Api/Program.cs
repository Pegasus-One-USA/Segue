using FHIRBridge.Api.Security;
using FHIRBridge.Application.Abstractions.Audit;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Security;
using FHIRBridge.Application.Services;
using FHIRBridge.Infrastructure.Persistence;
using FHIRBridge.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpContextAccessor();
builder.Services.AddControllers();
builder.Services.AddFhirBridgeAuthentication(builder.Configuration, builder.Environment);

// ── Authorization ────────────────────────────────────────────────────────────

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(AuthorizationPolicies.UnifiedAdmin, policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.AddRequirements(new UnifiedAdminRequirement());
    });

    // Permission-based policies — one per permission code.
    foreach (var code in AllPermissionCodes())
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

builder.Services.AddScoped<IAuthorizationHandler, UnifiedAdminAuthorizationHandler>();
builder.Services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();

// ── Security / identity ──────────────────────────────────────────────────────

builder.Services.AddScoped<ICurrentUserService, HttpContextCurrentUserService>();
builder.Services.AddScoped<IAccessTokenIssuer, JwtAccessTokenIssuer>();
builder.Services.AddScoped<IPasswordHasher, Pbkdf2PasswordHasher>();

// ── Persistence ───────────────────────────────────────────────────────────────

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(connectionString))
{
    builder.Services.AddDbContext<FHIRBridgeDbContext>(opt =>
        opt.UseInMemoryDatabase("FHIRBridgeDev"));

    builder.Services.AddScoped<IUserAccessRepository, InMemoryUserAccessRepository>();
    builder.Services.AddScoped<ITenantConfigurationRepository, InMemoryTenantConfigurationRepository>();
}
else
{
    builder.Services.AddDbContext<FHIRBridgeDbContext>(opt =>
        opt.UseSqlServer(connectionString));

    builder.Services.AddScoped<IUserAccessRepository, EfUserAccessRepository>();
    builder.Services.AddScoped<ITenantConfigurationRepository, InMemoryTenantConfigurationRepository>();
}

// ── Audit services ────────────────────────────────────────────────────────────

builder.Services.AddScoped<IOperationalAuditService, EfOperationalAuditService>();
builder.Services.AddScoped<IUserActivityAuditService, EfUserActivityAuditService>();

// ── Application services ──────────────────────────────────────────────────────

builder.Services.AddScoped<IUserAccessService, UserAccessService>();
builder.Services.AddScoped<ILocalAuthService, LocalAuthService>();
builder.Services.AddScoped<IUserManagementService, UserManagementService>();
builder.Services.AddScoped<IRoleManagementService, RoleManagementService>();
builder.Services.AddScoped<ITenantRegistrationService, TenantRegistrationService>();
builder.Services.AddScoped<IIdentitySeedService, LocalIdentitySeedService>();

// ── Swagger ───────────────────────────────────────────────────────────────────

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

var app = builder.Build();

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
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "FHIRBridge API v1");
        options.RoutePrefix = "swagger";
    });
}

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

app.Run();

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

static IEnumerable<string> AllPermissionCodes()
{
    yield return UnifiedPermissions.TenantsRead;
    yield return UnifiedPermissions.TenantsWrite;
    yield return UnifiedPermissions.ConfigurationWrite;
    yield return UnifiedPermissions.PipelineExecute;
    yield return UnifiedPermissions.AuditLogsRead;
    yield return UnifiedPermissions.SourceConnectionsTest;
    yield return UnifiedPermissions.UserInvite;
    yield return UnifiedPermissions.UserView;
    yield return UnifiedPermissions.UserEdit;
    yield return UnifiedPermissions.UserDeactivate;
    yield return UnifiedPermissions.RoleCreate;
    yield return UnifiedPermissions.RoleEdit;
    yield return UnifiedPermissions.RoleDelete;
    yield return UnifiedPermissions.RoleAssign;
    yield return UnifiedPermissions.RoleView;
    yield return UnifiedPermissions.WorkflowCreate;
    yield return UnifiedPermissions.WorkflowEdit;
    yield return UnifiedPermissions.WorkflowDelete;
    yield return UnifiedPermissions.WorkflowRun;
    yield return UnifiedPermissions.WorkflowView;
    yield return UnifiedPermissions.TenantSettingsEdit;
    yield return UnifiedPermissions.TenantBillingView;
    yield return UnifiedPermissions.ReportView;
    yield return UnifiedPermissions.PayloadView;
}
