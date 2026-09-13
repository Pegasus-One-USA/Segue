using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using FHIRBridge.Api.Workflows;
using FHIRBridge.Api.Cors;
using FHIRBridge.Api.Hubs;
using FHIRBridge.Api.Security;
using FHIRBridge.Observability;
using FHIRBridge.Observability.Logging;
using Microsoft.AspNetCore.DataProtection;
using FHIRBridge.Application;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Exceptions;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Abstractions.Sources;
using FHIRBridge.Application.Security;
using FHIRBridge.Infrastructure.Terminology.Hapi;
using FHIRBridge.Application.Services;
using FHIRBridge.Application.Validation;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Infrastructure;
using FHIRBridge.Infrastructure.Messaging;
using FHIRBridge.Infrastructure.Persistence;
using FHIRBridge.Infrastructure.Persistence.Workflows;
using FHIRBridge.Infrastructure.Security;
using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Infrastructure.Workflows;
using FHIRBridge.SharedKernel.Exceptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.OpenApi;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Pulls ConnectionStrings/messaging-broker secrets from Azure Key Vault when KeyVault:UseAzureKeyVault is set
// — as early as possible, since ConnectionStrings:FHIRBridgeDb itself (read further below) is one of them. See
// KeyVaultConfigurationExtensions' remarks for what this does and doesn't cover, and its graceful-on-failure
// behavior.
builder.Configuration.AddFhirBridgeKeyVaultConfiguration();

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

// No-op unless the process is actually started by the Windows Service Control Manager (e.g. `dotnet run`
// and console execution are unaffected) — lets the same published output run standalone or as a service.
builder.Host.UseWindowsService(options => options.ServiceName = "FHIRBridge.Api");

builder.Host.UseSerilog((context, loggerConfig) =>
    loggerConfig.ConfigureFhirBridge(context.Configuration, "FHIRBridge.Api"));

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
// Data Protection backs the encrypted OAuth launch-context and state tokens (ILaunchTokenProtector) and the
// at-rest encryption of app-provisioned secrets (DbSecretStore). The key ring MUST be persisted or long-lived
// tokens — especially EHR-launch URLs, which the EHR stores and invokes much later — stop decrypting after a
// restart/redeploy and surface as "The launch context is invalid or has been tampered with." on launch.
//   • A MULTI-INSTANCE deployment MUST set DataProtection:KeyRingPath to SHARED, backed-up storage
//     (Azure Files, K8s PVC, etc.) so every node shares one ring.
//   • When no path is configured we still persist to a stable MACHINE-WIDE folder (never an ephemeral ring, and
//     never one scoped to the account this process happens to run as) so single-instance restarts keep working
//     even if the service identity changes; a multi-instance deployment without a shared path is warned.
var dataProtection = builder.Services.AddDataProtection()
    .SetApplicationName(builder.Configuration["DataProtection:ApplicationName"] ?? "FHIRBridge");

// Shared with the Worker host (DataProtectionKeyRingPathResolver) precisely so the two can never compute
// different paths — see that class's remarks for what a forked ring costs.
var keyRingResolution = DataProtectionKeyRingPathResolver.Resolve(builder.Configuration["DataProtection:KeyRingPath"]);
var keyRingPath = keyRingResolution.Path;

if (keyRingResolution.Warning is not null && !builder.Environment.IsDevelopment())
{
    Console.Error.WriteLine($"[WARN] {keyRingResolution.Warning}");
}
else if (!keyRingResolution.WasConfigured && !builder.Environment.IsDevelopment())
{
    Console.Error.WriteLine(
        $"[WARN] DataProtection:KeyRingPath is not set; using the machine-wide key ring at '{keyRingPath}'. " +
        "Every host on this machine shares it, but a MULTI-INSTANCE deployment must set an explicitly shared, " +
        "persistent path or OAuth/launch tokens will not be decryptable across instances.");
}

Directory.CreateDirectory(keyRingPath);
dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keyRingPath));

// HIPAA #11: encrypt the key ring at rest with a certificate. Wraps (does not rotate/regenerate) the existing
// keys — Data Protection decides per-key how to decrypt based on that key's own stored descriptor, so keys
// written before this was configured stay readable; only newly generated keys get certificate-protected.
// Conditioned on a configured cert (never required in Development) so local/docker-compose startup, which has
// no certificate provisioned, is unaffected.
var dataProtectionCertPath = builder.Configuration["DataProtection:CertificatePath"];
if (!string.IsNullOrWhiteSpace(dataProtectionCertPath) && !builder.Environment.IsDevelopment())
{
    var dataProtectionCertPassword = builder.Configuration["DataProtection:CertificatePassword"];
    var dataProtectionCert = string.IsNullOrEmpty(dataProtectionCertPassword)
        ? System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificateFromFile(dataProtectionCertPath)
        : System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12FromFile(
            dataProtectionCertPath, dataProtectionCertPassword);
    dataProtection.ProtectKeysWithCertificate(dataProtectionCert);
}

// Alternative to the certificate above: wraps the key ring using an Azure Key Vault Key's wrap/unwrap
// operations instead of a local certificate. Same "wraps, never rotates" semantics — each key's own stored
// descriptor governs how it's decrypted, so keys written before this was configured stay readable. Requires
// the app's identity to hold the Key Vault "Key Vault Crypto User" role on the referenced key (a different
// role than "Key Vault Secrets Officer", which the tenant/app secret system uses). Off by default; set
// DataProtection:KeyVaultKeyId (e.g. https://<vault>.vault.azure.net/keys/<key-name>) to enable.
var dataProtectionKeyVaultKeyId = builder.Configuration["DataProtection:KeyVaultKeyId"];
if (!string.IsNullOrWhiteSpace(dataProtectionKeyVaultKeyId))
{
    dataProtection.ProtectKeysWithAzureKeyVault(new Uri(dataProtectionKeyVaultKeyId), new Azure.Identity.DefaultAzureCredential());
}

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Segue API",
        Version = "v1",
        Description = "REST API for Segue — FHIR data integration and transformation platform."
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
// Lets InteractiveSourceAuthorizationService re-stamp the correlation id on the /oauth/callback leg, whose
// workflow + session only become known once the encrypted OAuth state is decrypted (see IRequestCorrelationStamper).
builder.Services.AddScoped<IRequestCorrelationStamper, HttpContextRequestCorrelationStamper>();
builder.Services.AddScoped<IAccessTokenIssuer, JwtAccessTokenIssuer>();
builder.Services.AddScoped<IAuthorizationHandler, UnifiedAdminAuthorizationHandler>();
builder.Services.AddScoped<IAuthorizationHandler, SuperAdminOnlyAuthorizationHandler>();
builder.Services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
builder.Services.AddScoped<IAuthorizationHandler, WorkflowModuleAccessAuthorizationHandler>();
builder.Services.AddScoped<IAuthorizationHandler, MappingCatalogAccessAuthorizationHandler>();
builder.Services.AddScoped<IAuthorizationHandler, PermissionCatalogAccessAuthorizationHandler>();
builder.Services.AddScoped<IAuthorizationHandler, SourceDiscoveryAccessAuthorizationHandler>();
builder.Services.AddScoped<IAuthorizationHandler, GenericConnectionPermissionAuthorizationHandler>();
// Custom pipeline/API metrics + (when configured) OTLP/Azure Monitor export — was built but never actually called
// from either host, so IPipelineMetrics/IApiMetrics silently no-op'd (optional dependency) and OTel never exported
// anything. Always registers the in-process singletons the API Analytics/System Health screens read regardless
// of whether an exporter is configured (see ObservabilityOptions.Enabled's remarks).
builder.Services.AddFhirBridgeObservability(builder.Configuration, "FHIRBridge.Api");

builder.Services
    .AddFHIRBridgeApplication()
    .AddFHIRBridgeInfrastructure(builder.Configuration)
    .AddWorkflowCore()
    .AddWorkflowInfrastructure();

// The 13 IHapi{Code}TerminologySyncService implementations were previously registered only in the Worker
// host (see Worker/Program.cs), since only the scheduled workers called them. The new "Run Now" endpoint
// (HapiTerminologyConfigurationController) needs to resolve the same services from this host too. All 13
// are Scoped: every one of them now depends on HapiLocalTerminologyWriter (Scoped, holds a DbContext),
// so a Singleton registration here would be a captive-dependency DI validation failure at startup.
builder.Services.AddHttpClient();
builder.Services.AddScoped<IHapiCvxTerminologySyncService, HapiCvxTerminologySyncService>();
builder.Services.AddScoped<IHapiDcmTerminologySyncService, HapiDcmTerminologySyncService>();
builder.Services.AddScoped<IHapiHcpcsTerminologySyncService, HapiHcpcsTerminologySyncService>();
builder.Services.AddScoped<IHapiIcd10TerminologySyncService, HapiIcd10TerminologySyncService>();
builder.Services.AddScoped<IHapiIcd10PcsTerminologySyncService, HapiIcd10PcsTerminologySyncService>();
builder.Services.AddScoped<IHapiIcd11TerminologySyncService, HapiIcd11TerminologySyncService>();
builder.Services.AddScoped<IHapiIcpc3TerminologySyncService, HapiIcpc3TerminologySyncService>();
builder.Services.AddScoped<IHapiLoincTerminologySyncService, HapiLoincTerminologySyncService>();
builder.Services.AddScoped<IHapiMeshTerminologySyncService, HapiMeshTerminologySyncService>();
builder.Services.AddScoped<IHapiNdcTerminologySyncService, HapiNdcTerminologySyncService>();
builder.Services.AddScoped<IHapiRxNormTerminologySyncService, HapiRxNormTerminologySyncService>();
builder.Services.AddScoped<IHapiSnomedTerminologySyncService, HapiSnomedTerminologySyncService>();
builder.Services.AddScoped<IHapiUcumTerminologySyncService, HapiUcumTerminologySyncService>();

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

// Field-level lineage capture's consumer also runs in this host, not just the Worker (see
// LineageCaptureProcessor's remarks): POST /workflows/{id}/run executes the whole pipeline synchronously here,
// and the InMemory messaging provider's channel is in-process-only — without a local consumer, lineage
// published during a manual/interactive run would sit unread forever.
builder.Services.AddHostedService<LineageCaptureProcessor>();

// Backs RunStatusHub — pushes workflow-run status changes (Running/Succeeded/Failed) to the Dashboard and
// Workflow List live, instead of those screens only ever finding out on their next REST poll. See IRunStatusNotifier's
// remarks: only registered in this host, so RankedWorkflowOrchestrator resolves it as null (and simply skips the
// live push) wherever it isn't — e.g. the Worker process, which has no hub of its own to push into.
builder.Services.AddSignalR();
builder.Services.AddSingleton<IRunStatusNotifier, SignalRRunStatusNotifier>();

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

    // View-level Workflow module access: workflow.view OR any workflow-node permission (see
    // WorkflowModuleAccessAuthorizationHandler). Used only on the view-level workflow endpoints —
    // build/copy/run/delete keep their own literal workflow.create/edit/delete/run policy below,
    // unaffected by this one.
    options.AddPolicy(AuthorizationPolicies.WorkflowModuleAccess, policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.AddRequirements(new WorkflowModuleAccessRequirement());
    });

    // UnifiedAdmin OR "transformationrules.write" (see MappingCatalogAccessAuthorizationHandler) — the FHIR
    // element catalog is read-only reference metadata, not tenant configuration, so it's gated more loosely
    // than the rest of MappingController.
    options.AddPolicy(AuthorizationPolicies.MappingCatalogAccess, policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.AddRequirements(new MappingCatalogAccessRequirement());
    });

    // UnifiedAdmin OR "sourceconnections.create"/".edit" (see SourceDiscoveryAccessAuthorizationHandler) —
    // pre-create source endpoint discovery is part of the source-connection wizard, not a separate
    // admin-only capability.
    options.AddPolicy(AuthorizationPolicies.SourceDiscoveryAccess, policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.AddRequirements(new SourceDiscoveryAccessRequirement());
    });

    // UnifiedAdmin OR "role.view" (see PermissionCatalogAccessAuthorizationHandler) — the permission
    // catalog is read-only reference metadata needed to render the Role Permissions screen's read-only
    // grid, reachable with role.view alone. GetAll on the same controller keeps its own UnifiedAdmin-only
    // policy directly, unaffected by this one.
    options.AddPolicy(AuthorizationPolicies.PermissionCatalogAccess, policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.AddRequirements(new PermissionCatalogAccessRequirement());
    });

    // Permission-based policies — one per permission code declared in RbacSeedData.Permissions
    // or referenced via [StandardPermission] on a controller (see PermissionCatalog). A
    // sourceconnections.*/destinationconnections.* code gets the OR-vendor-permission composite
    // (GenericConnectionPermissionRequirement) instead of a plain single-code check — see that
    // type's own doc comment. Every other code is unaffected.
    foreach (var code in PermissionCatalog.AllPermissionCodes(typeof(Program).Assembly))
    {
        if (GenericConnectionPermissionRequirement.TryParse(code, out var genericGroup, out var genericAction))
        {
            options.AddPolicy(
                AuthorizationPolicies.HasPermission(code),
                policy =>
                {
                    policy.RequireAuthenticatedUser();
                    policy.AddRequirements(new GenericConnectionPermissionRequirement(genericGroup, genericAction));
                });
            continue;
        }

        options.AddPolicy(
            AuthorizationPolicies.HasPermission(code),
            policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.AddRequirements(new PermissionRequirement(code));
            });
    }
});
builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, GovernanceAuditingAuthorizationMiddlewareResultHandler>();
// The "Portal" policy is built per-request by DynamicPortalCorsPolicyProvider from
// IAllowedCorsOriginsCache (Portal:AllowedOrigins config floor ∪ AllowedCorsOrigins DB rows), not a
// fixed WithOrigins(...) list — so a SuperAdmin adding/removing an origin via the admin screen takes
// effect on the next request, no restart. AddCors still registers CorsService; the provider below
// replaces the default ICorsPolicyProvider it would otherwise register.
builder.Services.AddCors();
builder.Services.AddSingleton<ICorsPolicyProvider, DynamicPortalCorsPolicyProvider>();
builder.Services.AddOptions<AllowedCorsOriginsOptions>()
    .Configure(options => options.RequireHttps = false);

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
// [EnableRateLimiting("auth")] / ("oauth") / ("webhook"). Limits are configurable under "RateLimiting:*",
// with a SystemSettings DB row (same key) overriding the appsettings value if present. The .NET rate
// limiter builds its partitioned limiters once at startup, so a DB override here takes effect on the
// next process restart, not live — see ISystemSettingsCache for knobs that apply without a restart.
using (var settingsBootstrapScope = builder.Services.BuildServiceProvider().CreateScope())
{
    var settingsCache = settingsBootstrapScope.ServiceProvider
        .GetRequiredService<FHIRBridge.Application.Abstractions.Caching.ISystemSettingsCache>();

    var authPermit = settingsCache.GetIntAsync(
        "RateLimiting:Auth:PermitPerWindow", builder.Configuration.GetValue<int?>("RateLimiting:Auth:PermitPerWindow") ?? 10, default).GetAwaiter().GetResult();
    var authWindowMinutes = settingsCache.GetIntAsync(
        "RateLimiting:Auth:WindowMinutes", builder.Configuration.GetValue<int?>("RateLimiting:Auth:WindowMinutes") ?? 5, default).GetAwaiter().GetResult();
    var oauthPermit = settingsCache.GetIntAsync(
        "RateLimiting:OAuth:PermitPerWindow", builder.Configuration.GetValue<int?>("RateLimiting:OAuth:PermitPerWindow") ?? 30, default).GetAwaiter().GetResult();
    var oauthWindowMinutes = settingsCache.GetIntAsync(
        "RateLimiting:OAuth:WindowMinutes", builder.Configuration.GetValue<int?>("RateLimiting:OAuth:WindowMinutes") ?? 5, default).GetAwaiter().GetResult();
    var webhookPermit = settingsCache.GetIntAsync(
        "RateLimiting:Webhook:PermitPerWindow", builder.Configuration.GetValue<int?>("RateLimiting:Webhook:PermitPerWindow") ?? 120, default).GetAwaiter().GetResult();
    var webhookWindowMinutes = settingsCache.GetIntAsync(
        "RateLimiting:Webhook:WindowMinutes", builder.Configuration.GetValue<int?>("RateLimiting:Webhook:WindowMinutes") ?? 1, default).GetAwaiter().GetResult();

    builder.Services.AddRateLimiter(options =>
    {
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

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
}

var app = builder.Build();

app.UseForwardedHeaders();

// Pushes the same CorrelationId every downstream consumer (HttpContextCurrentUserService, the exception
// handler below) resolves onto every Serilog line for this request — including routine sub-500 rejections,
// which deliberately never reach a governance table (see the exception handler's comment) and would
// otherwise be findable only by full-text-searching the exception message/path in Seq.
app.Use(async (context, next) =>
{
    // Before anything reads a correlation id, replace Kestrel's per-request TraceIdentifier with the id every
    // leg of one workflow attempt shares (see WorkflowCorrelationResolver). Without this each leg — validate-run,
    // token-status, the launch-url mint, /oauth/callback, /run — lands under its own id, so the SmartLaunchLogs
    // row for an EHR sign-in can never be found alongside the run it authorized. Returns null (leaving
    // TraceIdentifier alone) when the caller supplied an explicit X-Correlation-Id, or when this request carries
    // no session id to key on.
    var derivedCorrelationId = WorkflowCorrelationResolver.Resolve(context);
    if (derivedCorrelationId is not null)
    {
        context.TraceIdentifier = derivedCorrelationId;
    }

    var correlationId = context.Request.Headers["X-Correlation-Id"].FirstOrDefault() ?? context.TraceIdentifier;
    System.Diagnostics.Activity.Current?.SetTag("correlation_id", correlationId);
    using (Serilog.Context.LogContext.PushProperty("CorrelationId", correlationId))
    {
        await next();
    }
});

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

        var (status, message, trusted, fieldErrors, ruleConflicts) = MapException(feature.Error);

        // A non-5xx message is shown to the client only if it's trusted (author-written UserMessage) or passes the
        // client-safe filter. Otherwise it's null and the manager emits a generic category message. This is the
        // choke point that stops raw/HTML/technical exception text from ever leaking, no matter what was thrown.
        var clientMessage = status >= 500
            ? null
            : (trusted ? message : ClientSafeMessage(message));

        context.Response.StatusCode  = status;
        context.Response.ContentType = "application/json";

        if (feature.Error is FHIRBridge.SharedKernel.Exceptions.BulkExportConcurrencyLimitExceededException concurrencyLimitError)
        {
            context.Response.Headers.RetryAfter = ((int)Math.Ceiling(concurrencyLimitError.RetryAfter.TotalSeconds)).ToString();
        }

        // Below 500, MapException/FHIRBridgeException/NotFoundException already represent an expected, routine
        // domain outcome (wrong password, duplicate name, stale reference, expired token, RBAC-adjacent auth
        // rejection) — not an unexpected system fault. These are everyday user behavior, not incidents: many are
        // already recorded in their own dedicated audit trail (AuthenticationLog, SecurityEvent) by the caller
        // before it threw. Routing them into ErrorLogs too would flood Operations → Errors with non-actionable
        // noise and mislabel routine outcomes (e.g. "wrong password") as something needing vendor support. Mirrors
        // the pattern already used by WorkflowEndpoints.cs's inline FHIRBridgeException catch — skip the Global
        // Exception Manager (no ErrorLogs row, no reference id) and return the safe message directly.
        if (status < 500)
        {
            app.Logger.LogWarning(feature.Error, "Expected domain failure on {Path}.", context.Request.Path);

            // Not routed through CaptureAsync (no ErrorLogs row at ordinary severity — see comment above), but
            // still recorded at Severity "Informational" via the lightweight CaptureExpectedAsync path so the
            // rejection is findable by CorrelationId (e.g. Correlation Search) without appearing in the default
            // Operations → Errors view. No reference id is surfaced to the client — this is a backend trail only.
            var expectedExceptionManager = context.RequestServices.GetRequiredService<FHIRBridge.Governance.IGlobalExceptionManager>();
            var expectedCorrelationId = context.Request.Headers["X-Correlation-Id"].FirstOrDefault() ?? context.TraceIdentifier;
            var expectedActivity = System.Diagnostics.Activity.Current;
            _ = await expectedExceptionManager.CaptureExpectedAsync(
                new FHIRBridge.Governance.ExpectedFailure(feature.Error.GetType().Name, feature.Error.Message),
                new FHIRBridge.Governance.ExceptionContext(
                    Module: "Api",
                    Severity: "Informational",
                    CorrelationId: expectedCorrelationId,
                    EndpointId: $"{context.Request.Method} {context.Request.Path}",
                    RequestId: context.TraceIdentifier,
                    TraceId: expectedActivity?.TraceId.ToString(),
                    SpanId: expectedActivity?.SpanId.ToString()));

            await context.Response.WriteAsJsonAsync(new
            {
                error = clientMessage ?? "The request could not be processed.",
                message = clientMessage ?? "The request could not be processed.",
                fieldErrors,
                ruleConflicts,
            });
            return;
        }

        app.Logger.LogError(feature.Error, "Unhandled exception on {Path}.", context.Request.Path);

        // Phase 6A – Enterprise Global Exception Management: genuine (5xx) unexpected failures are funneled through
        // the Global Exception Manager, which assigns a unique ErrorReferenceId, classifies it, persists the full
        // technical detail (via IGovernanceLogger → ErrorLogs), and returns a safe, user-friendly report. No stack
        // trace or internal message is ever written to the response.
        var exceptionManager = context.RequestServices.GetRequiredService<FHIRBridge.Governance.IGlobalExceptionManager>();
        var correlationId = context.Request.Headers["X-Correlation-Id"].FirstOrDefault() ?? context.TraceIdentifier;
        var activity = System.Diagnostics.Activity.Current;

        var report = await exceptionManager.CaptureAsync(
            feature.Error,
            new FHIRBridge.Governance.ExceptionContext(
                Module: "Api",
                Severity: "Error",
                CorrelationId: correlationId,
                EndpointId: $"{context.Request.Method} {context.Request.Path}",
                RequestId: context.TraceIdentifier,
                TraceId: activity?.TraceId.ToString(),
                SpanId: activity?.SpanId.ToString(),
                UserFriendlyMessageOverride: clientMessage));

        // report.ErrorReferenceId is null when every persistence attempt inside CaptureAsync failed (e.g. the
        // database was unreachable) — nothing was actually written to ErrorLogs, so there is no id to offer the
        // client or Operations → Errors could ever resolve. Omit the header/field entirely rather than quoting
        // an orphaned id.
        if (report.ErrorReferenceId is not null)
        {
            context.Response.Headers["X-Error-Reference-Id"] = report.ErrorReferenceId;
        }

        // `error`/`message` keep the existing client contract (the Angular sanitizer reads them); the new
        // `errorReferenceId`/`correlationId`/`category` fields drive the Phase 6A friendly-error dialog.
        await context.Response.WriteAsJsonAsync(new
        {
            error = report.UserFriendlyMessage,
            message = report.UserFriendlyMessage,
            errorReferenceId = report.ErrorReferenceId,
            correlationId = report.CorrelationId,
            category = report.Category.ToString(),
        });
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
ProvisionAppSecrets(app);
SyncDiscoveredPermissions(app);

// Durable (SQL) counterpart to the Serilog request log below, for the workflow/launch API surface only — see
// InboundApiRequestLoggingMiddleware for why a refused request needs a row of its own. Placed above
// UseSerilogRequestLogging so it wraps the same span of pipeline: a request short-circuited by CORS/CSRF/auth,
// or refused by a public-launch gate, is exactly the one that must not go unrecorded.
app.UseMiddleware<InboundApiRequestLoggingMiddleware>();

// One structured event per HTTP request (method, path, status, duration) instead of the framework's several
// per-request lines. Placed before the static-file and auth middleware so it times the whole pipeline, including
// anything short-circuited by CORS/CSRF/auth — a 401 that never reaches a handler is exactly the request you
// need to see. Health-check and static-asset polling is dropped to Verbose so it doesn't drown the log.
app.UseSerilogRequestLogging(options =>
{
    options.GetLevel = (httpContext, elapsedMs, exception) =>
    {
        if (exception is not null || httpContext.Response.StatusCode >= 500)
        {
            return Serilog.Events.LogEventLevel.Error;
        }

        if (httpContext.Response.StatusCode >= 400)
        {
            return Serilog.Events.LogEventLevel.Warning;
        }

        var path = httpContext.Request.Path.Value ?? string.Empty;
        var isNoise = path.StartsWith("/health", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/_", StringComparison.Ordinal)
            || System.IO.Path.HasExtension(path);

        return isNoise ? Serilog.Events.LogEventLevel.Verbose : Serilog.Events.LogEventLevel.Information;
    };

    options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
    {
        // Only non-PHI request metadata. Query strings are deliberately NOT enriched: a FHIR search URL can carry
        // patient identifiers, and the PHI-masking enricher matches on property NAME, so it would not catch them
        // inside a single "QueryString" value.
        diagnosticContext.Set("RequestHost", httpContext.Request.Host.Value);
        diagnosticContext.Set("RequestScheme", httpContext.Request.Scheme);
        diagnosticContext.Set("ClientIp", httpContext.Connection.RemoteIpAddress?.ToString());
        diagnosticContext.Set("CorrelationId",
            httpContext.Request.Headers["X-Correlation-Id"].FirstOrDefault() ?? httpContext.TraceIdentifier);

        // The acting user, when authenticated — makes "who changed this configuration" answerable from the log
        // alone. Falls back to null for anonymous endpoints rather than inventing a value.
        if (httpContext.User?.Identity?.IsAuthenticated == true)
        {
            diagnosticContext.Set("UserId",
                httpContext.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                ?? httpContext.User.FindFirst("sub")?.Value);
        }
    };
});

// Serves the Angular portal's production build when it's been copied into wwwroot (see deploy/windows) —
// a no-op in local dev, where wwwroot doesn't exist and the portal runs separately via `ng serve`.
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapHealthChecks("/health");

app.UseCors("Portal");

// HIPAA #7: double-submit CSRF check. Cookie-based auth means the browser attaches the session cookie to
// cross-site requests too, so a state-changing request must also prove it came from our own page by echoing
// back the non-HttpOnly CSRF cookie as a header. Only enforced when the access-token cookie is actually
// present — a request still using the (legacy/transitional) Authorization header isn't cookie-authenticated
// and has nothing for a cross-site form/script to silently ride along on.
app.Use(async (context, next) =>
{
    var isStateChanging = HttpMethods.IsPost(context.Request.Method) ||
                          HttpMethods.IsPut(context.Request.Method) ||
                          HttpMethods.IsPatch(context.Request.Method) ||
                          HttpMethods.IsDelete(context.Request.Method);

    // Anonymous endpoints ([AllowAnonymous]: login/SSO/magic-link/setup, plus the anonymous public-launch OAuth
    // and public workflow /run endpoints that third-party apps like Demo_TestApp call cross-origin) do NOT
    // authenticate via the session cookie, so the double-submit CSRF check must not gate them. Otherwise a
    // stale/expired fhirbridge_access_token cookie left in the browser locks the user out of logging back IN
    // (the login POST itself gets 403'd), and an anonymous third-party POST is rejected for a CSRF token it can
    // never have. CSRF only protects cookie-AUTHENTICATED state-changing requests — authenticated endpoints
    // (e.g. internal/change-password) keep the check. GetEndpoint is populated here because WebApplication
    // auto-inserts UseRouting ahead of user middleware once endpoints are mapped.
    var isAnonymousEndpoint = context.GetEndpoint()?.Metadata.GetMetadata<IAllowAnonymous>() is not null;

    if (isStateChanging && !isAnonymousEndpoint &&
        context.Request.Path.StartsWithSegments("/api/v1") &&
        context.Request.Cookies.ContainsKey("fhirbridge_access_token"))
    {
        var cookieToken = context.Request.Cookies[FHIRBridge.Api.Controllers.V1.AuthController.CsrfCookieName];
        var headerToken = context.Request.Headers["X-CSRF-Token"].ToString();

        if (string.IsNullOrEmpty(cookieToken) || !string.Equals(cookieToken, headerToken, StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { message = "CSRF token missing or invalid." });
            return;
        }
    }

    await next();
});

// Static legal content (e.g. Terms and Conditions), served from disk under Content/legal at
// "/legal/<file>" — anonymous, deploy-mutable (replace the file without a rebuild), independent of the
// portal's wwwroot. Placed after UseCors so the portal can fetch it cross-origin in local dev (portal on
// :4200, API on :5000); in production both are served from the same origin via wwwroot. Read by the
// first-run setup screen's "Read Terms & Conditions" modal.
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(Path.Combine(app.Environment.ContentRootPath, "Content", "legal")),
    RequestPath = "/legal",
});

app.UseAuthentication();

// Each entry blocks every /api/v1 route except its own allowlist while its claim is "true" — e.g.
// must change password, or must finish MFA enrollment. One shared check so a third gate is just
// another entry here, not a third copy-pasted middleware block.
var sessionGates = new[]
{
    (Claim: "pwd_change_required",
     Allowed: new[] { "/api/v1/auth/internal/change-password", "/api/v1/auth/me", "/api/v1/auth/refresh", "/api/v1/auth/internal/login", "/api/v1/auth/logout" },
     Message: "Password change is required before using Segue."),
    (Claim: "mfa_setup_required",
     Allowed: new[] { "/api/v1/auth/mfa", "/api/v1/auth/me", "/api/v1/auth/refresh", "/api/v1/auth/internal/login", "/api/v1/auth/logout" },
     Message: "Two-factor authentication setup is required before using Segue."),
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
// SystemSettings DB override (same key) takes effect on the next restart, same as the permit/window
// values configured above.
bool rateLimitingEnabled;
using (var rateLimitingSettingsScope = app.Services.CreateScope())
{
    var settingsCache = rateLimitingSettingsScope.ServiceProvider
        .GetRequiredService<FHIRBridge.Application.Abstractions.Caching.ISystemSettingsCache>();
    rateLimitingEnabled = settingsCache.GetBoolAsync(
        "RateLimiting:Enabled", app.Configuration.GetValue("RateLimiting:Enabled", true), default).GetAwaiter().GetResult();
}

if (rateLimitingEnabled)
{
    app.UseRateLimiter();
}
app.MapControllers();
app.MapWorkflowEndpoints();
app.MapHub<RunStatusHub>("/hubs/run-status").RequireAuthorization();

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

    // Startup runs before the host is listening, so a slow or failed step here shows up only as "the service
    // didn't come up". These events make a first deploy legible: which step ran, how long it took, and which
    // migrations were actually applied.
    var logger = app.Logger;
    var bootstrapStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();

    var dbContext = scope.ServiceProvider.GetService<FHIRBridgeDbContext>();
    if (dbContext is not null)
    {
        // Enumerated before migrating so the log names the migrations this boot is about to apply — afterwards
        // the pending list is empty and the information is gone. Best-effort: an unreachable database throws
        // here just as Migrate() would a line later, so it must not change the failure the operator sees.
        string[] pendingMigrations;
        try
        {
            pendingMigrations = dbContext.Database.GetPendingMigrations().ToArray();
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Could not enumerate pending EF migrations before applying them: {FailureReason}", exception.Message);
            pendingMigrations = [];
        }

        var migrationStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            dbContext.Database.Migrate();

            logger.Log(
                pendingMigrations.Length > 0 ? LogLevel.Information : LogLevel.Debug,
                "Database migration completed in {ElapsedMs}ms: {PendingMigrationCount} migration(s) applied [{PendingMigrations}].",
                (long)System.Diagnostics.Stopwatch.GetElapsedTime(migrationStartedAt).TotalMilliseconds,
                pendingMigrations.Length,
                string.Join(", ", pendingMigrations));
        }
        catch (Exception exception)
        {
            // Rethrown — a failed migration must still abort startup. Logged first because at this point in
            // boot the Serilog file/Seq sinks are configured but nothing else will describe what was being
            // attempted, and "service won't start" is otherwise the only symptom.
            logger.LogCritical(
                exception,
                "Database migration FAILED after {ElapsedMs}ms while applying {PendingMigrationCount} migration(s) "
                + "[{PendingMigrations}]. The application will not start.",
                (long)System.Diagnostics.Stopwatch.GetElapsedTime(migrationStartedAt).TotalMilliseconds,
                pendingMigrations.Length,
                string.Join(", ", pendingMigrations));
            throw;
        }
    }

    var bootstrapper = scope.ServiceProvider.GetService<IRbacBootstrapper>();
    bootstrapper?.EnsureAsync(CancellationToken.None).GetAwaiter().GetResult();

    // Runs every registered vendor endpoint-directory seeder (currently just Epic's) — adding a new vendor never
    // touches this call site, only DependencyInjection.cs's registration list.
    var seederCount = 0;
    foreach (var seeder in scope.ServiceProvider.GetServices<IEhrEndpointDirectorySeeder>())
    {
        seeder.EnsureAsync(CancellationToken.None).GetAwaiter().GetResult();
        seederCount++;
    }

    // One-time: populate SystemSettings with the value each DB-backed config key is already effectively
    // using (appsettings/code default) — so shipping this feature changes zero behavior until an admin
    // edits a row. Insert-only; never overwrites a row an admin has since customized.
    var systemSettingsSeeder = scope.ServiceProvider.GetService<ISystemSettingsSeeder>();
    systemSettingsSeeder?.EnsureSeededAsync(CancellationToken.None).GetAwaiter().GetResult();

    logger.LogInformation(
        "Database bootstrap completed in {ElapsedMs}ms. RbacBootstrapper={RbacBootstrapperRan} "
        + "EndpointDirectorySeeders={EndpointDirectorySeederCount} SystemSettingsSeeder={SystemSettingsSeederRan}",
        (long)System.Diagnostics.Stopwatch.GetElapsedTime(bootstrapStartedAt).TotalMilliseconds,
        bootstrapper is not null,
        seederCount,
        systemSettingsSeeder is not null);

    // De-identification profiles/rules are no longer auto-seeded on a fresh environment — de-identification
    // policy is now a deliberate, explicitly-authored decision (created via the mapping screen's
    // "De-identification" tab or Settings > Transformation Rules), not a silent default nobody at the
    // tenant reviewed. Left here, commented, rather than deleted: DeIdentificationProfileSeeder itself is
    // unchanged and insert-only, so re-enabling this call is a safe, reversible one-line change if the
    // decision changes.
    // var deIdentificationProfileSeeder = scope.ServiceProvider.GetService<IDeIdentificationProfileSeeder>();
    // deIdentificationProfileSeeder?.EnsureSeededAsync(CancellationToken.None).GetAwaiter().GetResult();
}

// Generates and persists the JWT signing key / download-link signing secret the first time an install has
// none (see AppSecretProvisioner's remarks) — must run after BootstrapDatabase, since the provisioned value
// is stored via DbSecretStore, which needs the ProvisionedSecrets table to already exist.
static void ProvisionAppSecrets(WebApplication app)
{
    AppSecretProvisioner.ProvisionAsync(app.Services, CancellationToken.None).GetAwaiter().GetResult();
}

// Reflection discovers every [StandardPermission] code in use (see PermissionCatalog), but only
// registering an in-memory authorization policy for it isn't enough to let anyone through — the
// code also has to exist as a Permission row before any role can be granted it. This closes that
// gap automatically at startup instead of requiring a manual PermissionConfiguration + migration
// edit for every new permission-gated feature. The actual synchronization logic lives in
// FHIRBridge.Api.Rbac.DiscoveredPermissionSynchronizer (RBAC redesign Step 5) — pulled out of this file
// so it's directly unit-testable without a WebApplicationFactory host.
static void SyncDiscoveredPermissions(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var repository = scope.ServiceProvider.GetService<IUserAccessRepository>();
    if (repository is null)
    {
        return;
    }

    FHIRBridge.Api.Rbac.DiscoveredPermissionSynchronizer.SyncAsync(repository, app.Logger).GetAwaiter().GetResult();
}

// Returns the HTTP status, a candidate client message, whether that message is TRUSTED (author-written and safe
// to show verbatim), and — only for RequestValidationException — the field-keyed messages a FluentValidation
// check produced. Only FHIRBridgeException.UserMessage is trusted; every other message is derived from a raw
// exception and MUST pass ClientSafeMessage before it can reach a client (see the exception handler).
static (int status, string message, bool trusted, IReadOnlyDictionary<string, string[]>? fieldErrors, IReadOnlyList<TransformationRuleTypeConflict>? ruleConflicts) MapException(Exception ex)
{
    // Field-shaped input validation (FluentValidation) — the only branch that carries fieldErrors, so the UI can
    // map a rejection back onto the specific control that caused it instead of just a flat message.
    if (ex is RequestValidationException rve)
        return (StatusCodes.Status400BadRequest, rve.UserMessage, true, rve.FieldErrors, rve.RuleConflicts);

    // FHIRBridgeException subtypes are deliberate, client-safe domain failures. UserMessage (not Message) is the
    // author-written text intended for end users — Message keeps the entity name + raw id for logs only.
    if (ex is NotFoundException nfe)
        return (StatusCodes.Status404NotFound, nfe.UserMessage, true, null, null);
    if (ex is FHIRBridge.SharedKernel.Exceptions.BulkExportConcurrencyLimitExceededException concurrencyLimitException)
        return (StatusCodes.Status429TooManyRequests, concurrencyLimitException.UserMessage, true, null, null);
    if (ex is FHIRBridgeException fbe)
        return (StatusCodes.Status400BadRequest, fbe.UserMessage, true, null, null);

    // A parent/cohort-seeding resource type (e.g. Patient) wasn't authorized for this app, so the whole workflow
    // run was cancelled up front — a short, author-written (trusted) message naming the resource type, rather
    // than the full inner FHIR error text, so it survives ClientSafeMessage's length/shape filter intact.
    if (ex is FHIRBridge.Runtime.Domain.Exceptions.WorkflowRunCancelledException workflowCancelled)
        return (
            StatusCodes.Status422UnprocessableEntity,
            $"Workflow cancelled: this app is not authorized for '{workflowCancelled.ResourceType}', which is " +
            "required as the parent/cohort scope for this workflow. Check the correlation id for full details.",
            true,
            null,
            null);

    if (ex is not InvalidOperationException and not UnauthorizedAccessException and not ArgumentException)
        return (StatusCodes.Status500InternalServerError, "An unexpected error occurred.", true, null, null);

    if (ex is UnauthorizedAccessException || ex is ArgumentException a && a.Message.Contains("unauthorized"))
        return (StatusCodes.Status401Unauthorized, ex.Message, false, null, null);

    var msg = ex.Message;

    // The status classification below still inspects the message, but the message itself is returned UNTRUSTED —
    // the handler runs it through ClientSafeMessage, so a raw/technical/HTML payload never reaches the client even
    // if its text happens to contain one of these substrings (e.g. an upstream HTML 404 page contains "not found").
    if (msg.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
        msg.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
        return (StatusCodes.Status404NotFound, msg, false, null, null);

    if (msg.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        return (StatusCodes.Status409Conflict, msg, false, null, null);

    if (msg.Contains("invalid or expired", StringComparison.OrdinalIgnoreCase) ||
        msg.Contains("email or password", StringComparison.OrdinalIgnoreCase) ||
        msg.Contains("Current password is invalid", StringComparison.OrdinalIgnoreCase))
        return (StatusCodes.Status401Unauthorized, msg, false, null, null);

    return (StatusCodes.Status400BadRequest, msg, false, null, null);
}

// The single guardrail that makes raw exception text safe-by-construction — see FHIRBridge.Governance.SafeErrorText.
// An untrusted message reaches a client only if it looks like a short, human-written sentence; markup, stack traces,
// multi-line, and oversized payloads are rejected (null → the caller substitutes a generic message). Reused by the
// bypassing controllers too, so NO future `throw new SomeException(rawBody)` can leak through any path. Lives in the
// Governance building block (not Api-only) so Infrastructure call sites (ConfiguredPipelineService,
// SourceConnectionTestService) can sanitize the same way without a layering violation.
static string? ClientSafeMessage(string? raw) => FHIRBridge.Governance.SafeErrorText.Sanitize(raw);
