using System.Collections.Concurrent;
using System.Text.Json;
using System.Text;
using HealthAppBackend;
using Microsoft.EntityFrameworkCore;

// Where the Demo frontend's build output lives — UseDefaultFiles()/UseStaticFiles()/
// MapFallbackToFile() below all resolve against WebRootPath automatically. Read from an env var
// (rather than full IConfiguration, which isn't available yet at this point) so deployments can
// point it at any folder — a sibling directory, not just one nested under this app's own content
// root — without a code change. Defaults to a "portal" folder next to the app for local/dev use.
// Accepts either a relative or absolute path; ASP.NET Core uses an absolute value as-is.
var webRootPath = Environment.GetEnvironmentVariable("DEMOAPP_PORTAL_PATH") ?? "portal";
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, WebRootPath = webRootPath });

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

var allowedFrontendOrigin = builder.Configuration["AllowedFrontendOrigin"] ?? "http://localhost:5501";
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? "Server=localhost,1433;Database=HealthAppDb;User Id=sa;Password=Your_password123;TrustServerCertificate=True";

builder.Services.AddDbContext<HealthAppDbContext>(options => options.UseSqlServer(connectionString));

// BackendSystem Patient List / Patient Details data-source switch (SQL Server / MySQL / NoSQL) — see
// PatientDataSourceReaders.cs. Scoped (not Singleton) because SqlPatientDataSourceReader depends on the scoped
// HealthAppDbContext; the MySQL/Mongo readers open their own connection per call, so scope has no real effect on
// them beyond matching the others.
builder.Services.AddScoped<SqlPatientDataSourceReader>();
builder.Services.AddScoped<MySqlPatientDataSourceReader>();
builder.Services.AddScoped<MongoPatientDataSourceReader>();
builder.Services.AddScoped<PatientDataSourceResolver>();
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<EpicSessionStore>();
builder.Services.AddSingleton<ProviderStandaloneCallerIdStore>();
// Access tokens issued by this app's own client-credentials endpoint (see DataLakeWebhookEndpoints).
// Singleton so the token endpoint and the webhook receiver see the same set.
builder.Services.AddSingleton<DataLakeTokenStore>();
builder.Services.AddHttpClient("Workflow");
builder.Services.AddCors(options => options.AddPolicy("Frontend", policy => policy
    .WithOrigins(allowedFrontendOrigin)
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.OpenApiInfo
    {
        Title = "HealthApp Demo Backend",
        Version = "v1",
        Description = "Third-party demo client backend — calls a configured Workflow URL (FHIRBridge or a stand-in) " +
            "and ingests the returned FHIR resources into its own database. Session auth uses an HttpOnly cookie " +
            "(hb_session), not a bearer token — exercise the /api/login endpoint first via 'Try it out'."
    });
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    // EnsureCreated() only creates the schema when the database doesn't exist yet — it does NOT add columns to an
    // already-existing HealthAppDb. Adding a field to any entity here (e.g. WorkflowSettingsEntity) requires every
    // dev/tester with a pre-existing local database to either drop it or manually ALTER TABLE the new column(s) in;
    // otherwise the first request touching that entity throws SqlException: Invalid column name '...'. There are no
    // EF migrations for this project (see HealthAppDbContext) — this app is not meant to model real schema evolution.
    var db = scope.ServiceProvider.GetRequiredService<HealthAppDbContext>();
    db.Database.EnsureCreated();

    // Generic, model-driven fallback for everything EnsureCreated() can't do to an already-existing
    // database (add a table a newer entity introduced, add a column a newer property introduced) --
    // see DatabaseSchemaReconciler's own remarks. Runs before the two reshape/seed blocks below so
    // they see a schema that already has every table/column the current model expects.
    DatabaseSchemaReconciler.Reconcile(db, scope.ServiceProvider.GetRequiredService<ILogger<Program>>());

    // EnsureCreated won't add the New 11 workflow-settings table to an already-existing HealthAppDb, so ensure it
    // exists (single row, List + Details URL per role, business-named columns) and has its seed row here.
    // Idempotent — safe every startup, a no-op on a brand-new DB where EnsureCreated already built the table. Also
    // drops the previous per-role shape ('Resource11WorkflowSetting', singular) if it lingers from an earlier build.
    db.Database.ExecuteSqlRaw(@"
IF EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Resource11WorkflowSetting')
    DROP TABLE [Resource11WorkflowSetting];

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Resource11WorkflowSettings')
    CREATE TABLE [Resource11WorkflowSettings] (
        [Id] INT NOT NULL CONSTRAINT [PK_Resource11WorkflowSettings] PRIMARY KEY,
        [Patient_List_11]            NVARCHAR(1000) NOT NULL CONSTRAINT [DF_R11_PatL]  DEFAULT(''),
        [Patient_Details_11]         NVARCHAR(1000) NOT NULL CONSTRAINT [DF_R11_PatD]  DEFAULT(''),
        [Provider_List_11]           NVARCHAR(1000) NOT NULL CONSTRAINT [DF_R11_PrvL]  DEFAULT(''),
        [Provider_Details_11]        NVARCHAR(1000) NOT NULL CONSTRAINT [DF_R11_PrvD]  DEFAULT(''),
        [ProviderInApp_List_11]      NVARCHAR(1000) NOT NULL CONSTRAINT [DF_R11_PiaL]  DEFAULT(''),
        [ProviderInApp_Details_11]   NVARCHAR(1000) NOT NULL CONSTRAINT [DF_R11_PiaD]  DEFAULT(''),
        [BackendSystem_List_11]      NVARCHAR(1000) NOT NULL CONSTRAINT [DF_R11_BsL]   DEFAULT(''),
        [BackendSystem_Details_11]   NVARCHAR(1000) NOT NULL CONSTRAINT [DF_R11_BsD]   DEFAULT('')
    );

IF NOT EXISTS (SELECT 1 FROM [Resource11WorkflowSettings] WHERE [Id] = 1)
    INSERT INTO [Resource11WorkflowSettings]
        ([Id],[Patient_List_11],[Patient_Details_11],[Provider_List_11],[Provider_Details_11],[ProviderInApp_List_11],[ProviderInApp_Details_11],[BackendSystem_List_11],[BackendSystem_Details_11])
    VALUES (1,'','','','','','','','');
");

    // BackendSystemPractitionerImportWorkflowId (WorkflowSettings), every Practitioner column added
    // alongside the global Practitioner import flow, and AccountContextLinks itself are now all handled
    // generically by DatabaseSchemaReconciler.Reconcile above -- no per-column/per-table patch needed
    // here anymore. New entities/properties added to the model going forward need nothing added here
    // either; the reconciler picks them up automatically on next startup.
}

// Serves the Angular build copied into wwwroot/ at deploy time — this backend hosts its own
// frontend (same origin), so the app's hb_session cookie (SameSite=Lax) works without any
// cross-origin complications.
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseCors("Frontend");

// Swagger:Enabled lets ops turn Swagger on in Production (via appsettings.Production.json, which
// survives every deploy untouched — see deploy/windows/README.md) without a code change or redeploy.
if (app.Environment.IsDevelopment() || app.Configuration.GetValue("Swagger:Enabled", false))
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

const string SessionCookieName = "hb_session";
var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

app.MapPost("/api/login", async (LoginRequest request, HealthAppDbContext db, SessionStore sessions, HttpContext http) =>
{
    var user = await db.Users.FirstOrDefaultAsync(u => u.Email == request.Email);
    if (user is null || user.Password != request.Password)
    {
        return Results.Unauthorized();
    }

    var sessionId = Guid.NewGuid().ToString("N");
    var expiresUtc = DateTime.UtcNow.AddHours(1);
    sessions.Set(sessionId, user.Id, user.Email, user.Role, expiresUtc);

    http.Response.Cookies.Append(SessionCookieName, sessionId, new CookieOptions
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Lax,
        Expires = expiresUtc
    });

    return Results.Ok(new { email = user.Email, role = user.Role });
});

app.MapPost("/api/logout", (HttpContext http, SessionStore sessions) =>
{
    if (http.Request.Cookies.TryGetValue(SessionCookieName, out var sessionId))
    {
        sessions.Remove(sessionId);
        http.Response.Cookies.Delete(SessionCookieName);
    }

    return Results.Ok();
});

// Lets the frontend re-establish its (purely client-side) logged-in state on a fresh page load — e.g. after the
// OAuth workflow URL redirects back in a new tab — as long as the hb_session cookie is still valid. Without this,
// a still-logged-in user would be bounced to the login screen just because the Angular app rebooted from scratch.
app.MapGet("/api/session", (HttpContext http, SessionStore sessions) =>
{
    if (!http.Request.Cookies.TryGetValue(SessionCookieName, out var sessionId)
        || !sessions.TryGet(sessionId, out _, out var email, out var role))
    {
        return Results.Unauthorized();
    }

    return Results.Ok(new { email, role });
});

app.MapGet("/api/hospitals", async (HttpContext http, SessionStore sessions, HealthAppDbContext db) =>
{
    if (!TryGetSession(http, sessions, out _, out _))
    {
        return Results.Unauthorized();
    }

    var hospitals = await db.Hospitals
        .OrderBy(h => h.Name)
        .Select(h => new { h.Id, h.Name, h.OrganizationId })
        .ToListAsync();

    return Results.Ok(hospitals);
});

app.MapGet("/api/settings", async (HttpContext http, SessionStore sessions, HealthAppDbContext db) =>
{
    if (!TryGetSession(http, sessions, out _, out var role))
    {
        return Results.Unauthorized();
    }

    if (role != UserRoles.Admin)
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    var settings = await GetWorkflowSettingsAsync(db);
    return Results.Ok(new
    {
        workflowUrl = settings.WorkflowUrl,
        patientWorkflowId = settings.PatientWorkflowId,
        patientDetailWorkflowId = settings.PatientDetailWorkflowId,
        patientBaseUrl = settings.PatientBaseUrl,
        patientCsvExportWorkflowId = settings.PatientCsvExportWorkflowId,
        patientCsvEmailExportWorkflowId = settings.PatientCsvEmailExportWorkflowId,
        athenaPatientWorkflowId = settings.AthenaPatientWorkflowId,
        athenaPatientBaseUrl = settings.AthenaPatientBaseUrl,
        athenaEhrEndpointId = settings.AthenaEhrEndpointId,
        ecwPatientWorkflowId = settings.EcwPatientWorkflowId,
        ecwPatientBaseUrl = settings.EcwPatientBaseUrl,
        ecwEhrEndpointId = settings.EcwEhrEndpointId,
        standaloneWorkflowId = settings.StandaloneWorkflowId,
        standaloneDetailWorkflowId = settings.StandaloneDetailWorkflowId,
        standaloneBaseUrl = settings.StandaloneBaseUrl,
        ecwProviderStandaloneListWorkflowId = settings.EcwProviderStandaloneListWorkflowId,
        ecwProviderStandaloneDetailWorkflowId = settings.EcwProviderStandaloneDetailWorkflowId,
        ecwProviderStandaloneBaseUrl = settings.EcwProviderStandaloneBaseUrl,
        ecwProviderStandaloneEhrEndpointId = settings.EcwProviderStandaloneEhrEndpointId,
        providerInAppWorkflowId = settings.ProviderInAppWorkflowId,
        ecwProviderInAppWorkflowId = settings.EcwProviderInAppWorkflowId,
        backendSystemPractitionerImportWorkflowId = settings.BackendSystemPractitionerImportWorkflowId
    });
});

// Read-only, any authenticated role — lets PatientStandaloneLaunchService resolve its FHIRBridge workflow ids +
// base URL from the admin-configured settings (formerly a gitignored per-developer local file) without granting
// Patient access to the full Admin settings endpoint. Two distinct workflow ids come back: one for the patient
// list fetch, one for the per-patient detail fetch — each is its own independent FHIRBridge public-launch opt-in.
// csvExportWorkflowId/csvEmailExportWorkflowId back the "Download Patient Information"/"Email Patient Information"
// buttons — see launch-standalone-patient.ts's downloadPatientInformation/emailPatientInformation.
// athena*/ecw* fields back the screen's Epic/athenahealth/eCW vendor toggle — see launch-standalone-patient.ts's
// vendor signal — and are used only for the connect/list step, never detail/CSV export (those stay Epic-only).
app.MapGet("/api/patient-standalone-settings", async (HttpContext http, SessionStore sessions, HealthAppDbContext db) =>
{
    if (!TryGetSession(http, sessions, out _, out _))
    {
        return Results.Unauthorized();
    }

    var settings = await GetWorkflowSettingsAsync(db);
    return Results.Ok(new
    {
        workflowId = settings.PatientWorkflowId,
        detailWorkflowId = settings.PatientDetailWorkflowId,
        baseUrl = settings.PatientBaseUrl,
        csvExportWorkflowId = settings.PatientCsvExportWorkflowId,
        csvEmailExportWorkflowId = settings.PatientCsvEmailExportWorkflowId,
        athenaWorkflowId = settings.AthenaPatientWorkflowId,
        athenaBaseUrl = settings.AthenaPatientBaseUrl,
        athenaEhrEndpointId = settings.AthenaEhrEndpointId,
        ecwWorkflowId = settings.EcwPatientWorkflowId,
        ecwBaseUrl = settings.EcwPatientBaseUrl,
        ecwEhrEndpointId = settings.EcwEhrEndpointId
    });
});

app.MapPost("/api/settings", async (SaveSettingsRequest request, HttpContext http, SessionStore sessions, HealthAppDbContext db) =>
{
    if (!TryGetSession(http, sessions, out _, out var role))
    {
        return Results.Unauthorized();
    }

    if (role != UserRoles.Admin)
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    var settings = await db.WorkflowSettings.FindAsync(1);
    if (settings is null)
    {
        settings = new WorkflowSettingsEntity { Id = 1 };
        db.WorkflowSettings.Add(settings);
    }

    // .Trim() on a possibly-null field would NullReferenceException if a caller posts a body missing one of these
    // (e.g. an old cached frontend bundle still posting the pre-PatientWorkflowId/PatientBaseUrl shape) — System.Text.Json
    // deserializes a missing/null JSON property as null regardless of the record's non-nullable C# type.
    settings.WorkflowUrl = request.WorkflowUrl?.Trim() ?? string.Empty;
    settings.PatientWorkflowId = request.PatientWorkflowId?.Trim() ?? string.Empty;
    settings.PatientDetailWorkflowId = request.PatientDetailWorkflowId?.Trim() ?? string.Empty;
    settings.PatientBaseUrl = request.PatientBaseUrl?.Trim() ?? string.Empty;
    settings.PatientCsvExportWorkflowId = request.PatientCsvExportWorkflowId?.Trim() ?? string.Empty;
    settings.PatientCsvEmailExportWorkflowId = request.PatientCsvEmailExportWorkflowId?.Trim() ?? string.Empty;
    settings.AthenaPatientWorkflowId = request.AthenaPatientWorkflowId?.Trim() ?? string.Empty;
    settings.AthenaPatientBaseUrl = request.AthenaPatientBaseUrl?.Trim() ?? string.Empty;
    settings.AthenaEhrEndpointId = request.AthenaEhrEndpointId?.Trim() ?? string.Empty;
    settings.EcwPatientWorkflowId = request.EcwPatientWorkflowId?.Trim() ?? string.Empty;
    settings.EcwPatientBaseUrl = request.EcwPatientBaseUrl?.Trim() ?? string.Empty;
    settings.EcwEhrEndpointId = request.EcwEhrEndpointId?.Trim() ?? string.Empty;
    settings.StandaloneWorkflowId = request.StandaloneWorkflowId?.Trim() ?? string.Empty;
    settings.StandaloneDetailWorkflowId = request.StandaloneDetailWorkflowId?.Trim() ?? string.Empty;
    settings.StandaloneBaseUrl = request.StandaloneBaseUrl?.Trim() ?? string.Empty;
    settings.EcwProviderStandaloneListWorkflowId = request.EcwProviderStandaloneListWorkflowId?.Trim() ?? string.Empty;
    settings.EcwProviderStandaloneDetailWorkflowId = request.EcwProviderStandaloneDetailWorkflowId?.Trim() ?? string.Empty;
    settings.EcwProviderStandaloneBaseUrl = request.EcwProviderStandaloneBaseUrl?.Trim() ?? string.Empty;
    settings.EcwProviderStandaloneEhrEndpointId = request.EcwProviderStandaloneEhrEndpointId?.Trim() ?? string.Empty;
    settings.ProviderInAppWorkflowId = request.ProviderInAppWorkflowId?.Trim() ?? string.Empty;
    settings.EcwProviderInAppWorkflowId = request.EcwProviderInAppWorkflowId?.Trim() ?? string.Empty;
    settings.BackendSystemPractitionerImportWorkflowId = request.BackendSystemPractitionerImportWorkflowId?.Trim() ?? string.Empty;
    await db.SaveChangesAsync();

    return Results.Ok(new
    {
        workflowUrl = settings.WorkflowUrl,
        patientWorkflowId = settings.PatientWorkflowId,
        patientDetailWorkflowId = settings.PatientDetailWorkflowId,
        patientBaseUrl = settings.PatientBaseUrl,
        patientCsvExportWorkflowId = settings.PatientCsvExportWorkflowId,
        patientCsvEmailExportWorkflowId = settings.PatientCsvEmailExportWorkflowId,
        athenaPatientWorkflowId = settings.AthenaPatientWorkflowId,
        athenaPatientBaseUrl = settings.AthenaPatientBaseUrl,
        athenaEhrEndpointId = settings.AthenaEhrEndpointId,
        ecwPatientWorkflowId = settings.EcwPatientWorkflowId,
        ecwPatientBaseUrl = settings.EcwPatientBaseUrl,
        ecwEhrEndpointId = settings.EcwEhrEndpointId,
        standaloneWorkflowId = settings.StandaloneWorkflowId,
        standaloneDetailWorkflowId = settings.StandaloneDetailWorkflowId,
        standaloneBaseUrl = settings.StandaloneBaseUrl,
        ecwProviderStandaloneListWorkflowId = settings.EcwProviderStandaloneListWorkflowId,
        ecwProviderStandaloneDetailWorkflowId = settings.EcwProviderStandaloneDetailWorkflowId,
        ecwProviderStandaloneBaseUrl = settings.EcwProviderStandaloneBaseUrl,
        ecwProviderStandaloneEhrEndpointId = settings.EcwProviderStandaloneEhrEndpointId,
        providerInAppWorkflowId = settings.ProviderInAppWorkflowId,
        ecwProviderInAppWorkflowId = settings.EcwProviderInAppWorkflowId,
        backendSystemPractitionerImportWorkflowId = settings.BackendSystemPractitionerImportWorkflowId
    });
});

// Read-only, any authenticated role — lets launch-standalone-provider.ts's fetch/detail/redirect calls resolve the
// configured workflow ids + base URL without needing the full settings endpoint's role check.
app.MapGet("/api/provider-standalone-workflow-ids", async (HttpContext http, SessionStore sessions, HealthAppDbContext db) =>
{
    if (!TryGetSession(http, sessions, out _, out _))
    {
        return Results.Unauthorized();
    }

    var settings = await db.WorkflowSettings.FindAsync(1);
    return Results.Ok(new
    {
        standaloneWorkflowId = settings?.StandaloneWorkflowId ?? string.Empty,
        standaloneDetailWorkflowId = settings?.StandaloneDetailWorkflowId ?? string.Empty,
        standaloneBaseUrl = settings?.StandaloneBaseUrl ?? string.Empty,
        ecwProviderStandaloneListWorkflowId = settings?.EcwProviderStandaloneListWorkflowId ?? string.Empty,
        ecwProviderStandaloneDetailWorkflowId = settings?.EcwProviderStandaloneDetailWorkflowId ?? string.Empty,
        ecwProviderStandaloneBaseUrl = settings?.EcwProviderStandaloneBaseUrl ?? string.Empty,
        ecwProviderStandaloneEhrEndpointId = settings?.EcwProviderStandaloneEhrEndpointId ?? string.Empty
    });
});

// Anonymous by design (no session-cookie check): launch-provider-in-app.ts calls this from inside Epic's
// embedded ("Embedded" launch display mode) iframe, where the browser treats it as a third-party/cross-site
// request and won't attach the hb_session cookie (SameSite=Lax) — a session check here would silently 401 on
// every embedded EHR launch and fall back to an empty context. ProviderInAppWorkflowId is a raw workflow id (not
// a secret), so it's minted into a real, opaque launch-context token on every call via FHIRBridge's anonymous
// GET /api/v1/workflows/{id}/public-launch-context — this app never stores a pre-minted token itself, which is
// what used to let an admin accidentally paste the raw workflow id in its place (see
// HealthAppDbContext.ProviderInAppWorkflowId). The actual security boundary is still FHIRBridge's own
// ILaunchTokenProtector validation when the minted token is redeemed against the iss/launch exchange, plus the
// workflow having been opted into public launch via POST /api/v1/workflows/{id}/enable-public-launch. Must be
// awaited BEFORE the component's synchronous full-page redirect to FHIRBridge's launch endpoint (see ngOnInit).
app.MapGet("/api/provider-in-app-launch-context", async (
    HttpContext http,
    SessionStore sessions,
    HealthAppDbContext db,
    IHttpClientFactory httpClientFactory,
    ILogger<Program> logger,
    string? iss) =>
{
    var settings = await db.WorkflowSettings.FindAsync(1);
    // Auto-select the eCW Provider EMR workflow when the launching EHR's iss is an eCW practice (host *.ecwcloud.com)
    // and a separate eCW workflow id is configured; otherwise use the Epic ProviderInAppWorkflowId. Lets both
    // vendors share the one registered Launch URL, disambiguated by iss (see WorkflowSettingsEntity remarks).
    var isEcwLaunch = !string.IsNullOrWhiteSpace(iss)
        && iss.Contains("ecwcloud.com", StringComparison.OrdinalIgnoreCase);
    var workflowId = isEcwLaunch && !string.IsNullOrWhiteSpace(settings?.EcwProviderInAppWorkflowId)
        ? settings!.EcwProviderInAppWorkflowId
        : settings?.ProviderInAppWorkflowId;
    var baseUrl = settings?.StandaloneBaseUrl ?? string.Empty;

    if (string.IsNullOrWhiteSpace(workflowId) || string.IsNullOrWhiteSpace(baseUrl))
    {
        return Results.Ok(new { providerLaunchContext = string.Empty, standaloneBaseUrl = baseUrl });
    }

    var client = httpClientFactory.CreateClient("Workflow");
    // Epic's embedded ("Embedded" launch display mode) iframe won't send the hb_session cookie at all (cross-site,
    // SameSite=Lax), so there's genuinely no logged-in account to read in that case — falls back to a single fixed
    // identity, same as before, so FHIRBridge still has something stable to permanently bind against. A real
    // browser tab hitting this same endpoint (e.g. testing Provider_InApp outside the iframe) DOES send the
    // cookie, so the actual logged-in HealthApp account's email is used instead whenever one is present — letting
    // more than one seeded account exercise this flow independently, each getting its own FHIRBridge binding.
    var providerInAppUserIdentity =
        http.Request.Cookies.TryGetValue(SessionCookieName, out var sessionId)
        && sessions.TryGet(sessionId, out _, out var sessionEmail, out _)
            ? sessionEmail
            : "providerinapp@healthapp.local";
    var mintUrl = $"{baseUrl.TrimEnd('/')}/api/v1/workflows/{workflowId}/public-launch-context?userIdentity={Uri.EscapeDataString(providerInAppUserIdentity)}";

    // Pre-flight this launch before handing the browser off to the EHR. An EHR launch has no natural "click" to
    // validate on — the flow starts at the EHR — so this is the only moment before the round trip begins. The
    // correlation id it mints is passed into the mint call below, which bakes it into the encrypted launch
    // context; /oauth/callback then restores it and continues the very row validate-run created, so an EHR launch
    // becomes one Execution History entry rather than a validated row plus a separate run.
    //
    // Best-effort: this app cannot usefully block a real EHR launch on its own pre-flight failing, and a refusal
    // is already recorded server-side as a ValidationFailed row.
    string? attemptCorrelationId = null;
    try
    {
        var validateResponse = await client.PostAsync(
            $"{baseUrl.TrimEnd('/')}/api/v1/workflows/{workflowId}/validate-run",
            new StringContent(
                JsonSerializer.Serialize(new { patientId = (string?)null, patientSearchCriteria = (string?)null, callerId = (string?)null }),
                Encoding.UTF8,
                "application/json"));

        if (validateResponse.IsSuccessStatusCode)
        {
            var validation = JsonSerializer.Deserialize<ValidateRunResult>(
                await validateResponse.Content.ReadAsStringAsync(), jsonOptions);
            attemptCorrelationId = validation?.CorrelationId;
        }
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "validate-run pre-flight could not be completed for ProviderInAppWorkflowId={WorkflowId}.", workflowId);
    }


    try
    {
        using var mintRequest = new HttpRequestMessage(HttpMethod.Get, mintUrl);
        if (!string.IsNullOrWhiteSpace(attemptCorrelationId))
        {
            mintRequest.Headers.TryAddWithoutValidation("X-Correlation-Id", attemptCorrelationId);
        }

        var response = await client.SendAsync(mintRequest);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "Could not mint a launch context for ProviderInAppWorkflowId={WorkflowId}: FHIRBridge returned {StatusCode}.",
                workflowId, (int)response.StatusCode);
            return Results.Ok(new { providerLaunchContext = string.Empty, standaloneBaseUrl = baseUrl });
        }

        var body = await response.Content.ReadAsStringAsync();
        var minted = JsonSerializer.Deserialize<MintedLaunchContext>(body, jsonOptions);
        return Results.Ok(new
        {
            providerLaunchContext = minted?.Context ?? string.Empty,
            standaloneBaseUrl = baseUrl
        });
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Could not reach FHIRBridge to mint a launch context for ProviderInAppWorkflowId={WorkflowId}.", workflowId);
        return Results.Ok(new { providerLaunchContext = string.Empty, standaloneBaseUrl = baseUrl });
    }
});

// Resolves the ProviderInApp workflow's LATEST fetched patient by workflow id — the "auto-fetch / Refresh"
// behaviour on the launch view (launch-provider-in-app.ts). No fresh EHR launch is needed: FHIRBridge already
// holds the data and a refreshable token, and its anonymous, read-only GET /workflows/{id}/latest-launch-result
// returns the most recent run's Patient (gated on the workflow being publicly launchable). Routes eCW vs Epic by
// the caller-supplied iss the same way the mint endpoint above does (and, absent an iss, prefers the eCW workflow
// when one is configured, since that's the vendor the caller most recently set up). Anonymous, like the mint.
app.MapGet("/api/provider-in-app-latest-result", async (
    HealthAppDbContext db,
    IHttpClientFactory httpClientFactory,
    ILogger<Program> logger,
    string? iss) =>
{
    var settings = await db.WorkflowSettings.FindAsync(1);
    var baseUrl = settings?.StandaloneBaseUrl ?? string.Empty;
    var isEcw = (!string.IsNullOrWhiteSpace(iss) && iss.Contains("ecwcloud.com", StringComparison.OrdinalIgnoreCase))
        || (string.IsNullOrWhiteSpace(iss) && !string.IsNullOrWhiteSpace(settings?.EcwProviderInAppWorkflowId));
    var useEcw = isEcw && !string.IsNullOrWhiteSpace(settings?.EcwProviderInAppWorkflowId);
    var workflowId = useEcw ? settings!.EcwProviderInAppWorkflowId : settings?.ProviderInAppWorkflowId;
    // Vendor is determined BY THE WORKFLOW ID this resolves to (which config field it came from) — reliable across
    // any browser, unlike the frontend's iss-derived sessionStorage guess.
    var vendor = useEcw ? "eClinicalWorks (eCW)" : "Epic Sandbox";

    if (string.IsNullOrWhiteSpace(workflowId) || string.IsNullOrWhiteSpace(baseUrl))
    {
        return Results.Ok(new { workflowRunId = (string?)null, vendor });
    }

    try
    {
        var client = httpClientFactory.CreateClient("Workflow");
        var response = await client.GetAsync($"{baseUrl.TrimEnd('/')}/api/v1/workflows/{workflowId}/latest-launch-result");
        if (!response.IsSuccessStatusCode)
        {
            return Results.Ok(new { workflowRunId = (string?)null, vendor });
        }

        var body = await response.Content.ReadAsStringAsync();
        var latest = JsonSerializer.Deserialize<LatestLaunchResult>(body, jsonOptions);
        return Results.Ok(new { workflowRunId = latest?.WorkflowRunId, vendor });
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Could not reach FHIRBridge for latest-launch-result of workflow {WorkflowId}.", workflowId);
        return Results.Ok(new { workflowRunId = (string?)null, vendor });
    }
});

// Account-linking check — see AccountContextLinkEntity's own remarks for what this enforces and why it lives
// here rather than (or on top of) FHIRBridge's own UserFhirContextBindings. Called by the frontend once a launch
// has produced a workflowRunId, right before displaying whatever patient FHIRBridge's /launch-result returns.
// Anonymous by design, same reasoning as /api/provider-in-app-launch-context above: an embedded EHR-launch
// iframe won't send the hb_session cookie at all, so audienceType=ehrLaunch has to tolerate no session present
// (skips the check entirely rather than 401ing — best-effort, matches how the launch-context minting side
// already treats this same limitation). audienceType=patientStandalone is a directly-opened flow that always
// has a session in practice, but is handled the same defensive way rather than assuming that.
app.MapGet("/api/account-context-link/check", async (
    HttpContext http,
    SessionStore sessions,
    HealthAppDbContext db,
    IHttpClientFactory httpClientFactory,
    ILogger<Program> logger,
    string workflowRunId,
    string audienceType) =>
{
    if (audienceType is not ("ehrLaunch" or "patientStandalone"))
    {
        return Results.BadRequest(new { error = "invalid_request", error_description = "audienceType must be ehrLaunch or patientStandalone." });
    }

    if (!http.Request.Cookies.TryGetValue(SessionCookieName, out var sessionId)
        || !sessions.TryGet(sessionId, out _, out var accountEmail, out _))
    {
        // No session at all (the embedded-iframe case, or a not-yet-logged-in tab) — nothing to link against.
        // Not an error: the caller should proceed exactly as if this check had never run.
        return Results.Ok(new { ok = true });
    }

    var settings = await db.WorkflowSettings.FindAsync(1);
    var baseUrl = settings?.StandaloneBaseUrl ?? string.Empty;
    if (string.IsNullOrWhiteSpace(baseUrl))
    {
        return Results.Ok(new { ok = true });
    }

    string? resourceId;
    try
    {
        var client = httpClientFactory.CreateClient("Workflow");
        var response = await client.GetAsync($"{baseUrl.TrimEnd('/')}/api/v1/workflows/runs/{workflowRunId}/launch-result");
        if (!response.IsSuccessStatusCode)
        {
            return Results.Ok(new { ok = true });
        }

        var body = await response.Content.ReadAsStringAsync();
        var launchResult = JsonSerializer.Deserialize<LaunchResultPatientId>(body, jsonOptions);
        resourceId = launchResult?.PatientId;
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Could not reach FHIRBridge to read launch-result for workflowRunId={WorkflowRunId}.", workflowRunId);
        return Results.Ok(new { ok = true });
    }

    if (string.IsNullOrWhiteSpace(resourceId))
    {
        // Nothing resolved yet (e.g. the run failed or returned no Patient) — nothing to check against.
        return Results.Ok(new { ok = true });
    }

    // Reverse direction: is this patient already linked to a DIFFERENT account? Checked for both audience types —
    // this is what stops a different account from claiming a patient another account already owns.
    var existingForResource = await db.AccountContextLinks.FirstOrDefaultAsync(
        x => x.AudienceType == audienceType && x.ResourceId == resourceId);
    if (existingForResource is not null && existingForResource.AccountEmail != accountEmail)
    {
        logger.LogWarning(
            "Account-context link mismatch: audienceType={AudienceType} — this patient is already linked to a different account.",
            audienceType);
        return Results.Ok(new { ok = false, message = "This patient is already linked to a different account. Please contact your administrator if you believe this is an error." });
    }

    // Forward direction (Patient Standalone only): is THIS account already linked to a DIFFERENT patient? Provider
    // EHR Launch deliberately skips this — a provider is expected to launch into many different patients' charts
    // over time, so the same account linking to many different patients is allowed there.
    if (audienceType == "patientStandalone")
    {
        var existingForAccount = await db.AccountContextLinks.FirstOrDefaultAsync(
            x => x.AudienceType == audienceType && x.AccountEmail == accountEmail);
        if (existingForAccount is not null && existingForAccount.ResourceId != resourceId)
        {
            logger.LogWarning(
                "Account-context link mismatch: audienceType={AudienceType} — this account is already linked to a different patient.",
                audienceType);
            return Results.Ok(new { ok = false, message = "This account is already linked to a different patient. Please contact your administrator if you believe this is an error." });
        }
    }

    if (existingForResource is null)
    {
        db.AccountContextLinks.Add(new AccountContextLinkEntity
        {
            AccountEmail = accountEmail,
            AudienceType = audienceType,
            ResourceId = resourceId,
            CreatedUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    return Results.Ok(new { ok = true });
});

// Calls the admin-configured workflow URL, expects a { "Resources": [{ "ResourceType", "ResourceId",
// "Payload" }] } body back, and upserts each resource into the local Patients table — demonstrating the
// app fetching real data through FHIRBridge and storing it in its own database.
app.MapPost("/api/workflow/run", async (
    HttpContext http,
    SessionStore sessions,
    HealthAppDbContext db,
    IHttpClientFactory httpClientFactory) =>
{
    if (!TryGetSession(http, sessions, out _, out _))
    {
        return Results.Unauthorized();
    }

    var settings = await db.WorkflowSettings.FindAsync(1);
    var workflowUrl = settings?.WorkflowUrl;
    if (string.IsNullOrWhiteSpace(workflowUrl))
    {
        return Results.Ok(new { status = "Failed", errorMessage = "No workflow URL is configured. Ask an admin to set one." });
    }

    var client = httpClientFactory.CreateClient("Workflow");

    HttpResponseMessage response;
    try
    {
        response = await client.PostAsync(workflowUrl, content: null);
    }
    catch (Exception ex)
    {
        return Results.Ok(new { status = "Failed", errorMessage = $"Could not reach the workflow URL: {ex.Message}" });
    }

    if (!response.IsSuccessStatusCode)
    {
        return Results.Ok(new { status = "Failed", errorMessage = $"Workflow call failed with status {(int)response.StatusCode}." });
    }

    ResourcesEnvelope? envelope;
    try
    {
        var body = await response.Content.ReadAsStringAsync();
        envelope = JsonSerializer.Deserialize<ResourcesEnvelope>(body, jsonOptions);
    }
    catch (JsonException ex)
    {
        return Results.Ok(new { status = "Failed", errorMessage = $"Unexpected workflow response: {ex.Message}" });
    }

    if (envelope?.Resources is null || envelope.Resources.Count == 0)
    {
        return Results.Ok(new { status = "Failed", errorMessage = "Workflow response contained no resources." });
    }

    var now = DateTime.UtcNow;
    foreach (var resource in envelope.Resources)
    {
        var fields = PatientFieldExtractor.Extract(resource.Payload);
        var existing = await db.Patients.FirstOrDefaultAsync(p => p.ResourceId == resource.ResourceId);

        if (existing is null)
        {
            existing = new PatientEntity
            {
                ResourceId = resource.ResourceId,
                RecordCreatedOn = now
            };
            db.Patients.Add(existing);
        }

        existing.ResourceType = resource.ResourceType;
        existing.Payload = resource.Payload;
        existing.FullName = fields.FullName;
        existing.FirstName = fields.FirstName;
        existing.MiddleName = fields.MiddleName;
        existing.LastName = fields.LastName;
        existing.Gender = fields.Gender;
        existing.LegalSex = fields.LegalSex;
        existing.SexForClinicalUse = fields.SexForClinicalUse;
        existing.Pronouns = fields.Pronouns;
        existing.BirthDate = fields.BirthDate;
        existing.MaritalStatus = fields.MaritalStatus;
        existing.PatientStatus = fields.PatientStatus;
        existing.Deceased = fields.Deceased;
        existing.UsCoreSex = fields.UsCoreSex;
        existing.Race = fields.Race;
        existing.Ethnicity = fields.Ethnicity;
        existing.Address = fields.Address;
        existing.City = fields.City;
        existing.State = fields.State;
        existing.PostalCode = fields.PostalCode;
        existing.Country = fields.Country;
        existing.HomePhone = fields.HomePhone;
        existing.MobilePhone = fields.MobilePhone;
        existing.Email = fields.Email;
        existing.PreferredLanguage = fields.PreferredLanguage;
        existing.GeneralPractitioner = fields.GeneralPractitioner;
        existing.ManagingOrganization = fields.ManagingOrganization;
        existing.UpdatedAtUtc = now;
    }

    await db.SaveChangesAsync();

    return Results.Ok(new { status = "Succeeded", recordsIngested = envelope.Resources.Count });
});

// Cards for the Records screen — one per patient, most recently inserted first, so the left/right
// carousel starts on the newest record. Ordered by Id (identity PK, always populated and monotonic)
// rather than RecordCreatedOn, since rows written directly by a real destination writer (bypassing
// this app's own /api/workflow/run ingestion) can leave RecordCreatedOn null.
app.MapGet("/api/patients", async (HttpContext http, SessionStore sessions, HealthAppDbContext db) =>
{
    if (!TryGetSession(http, sessions, out _, out _))
    {
        return Results.Unauthorized();
    }

    var patients = await db.Patients.OrderByDescending(p => p.Id).ToListAsync();
    var cards = patients.Select(ToCard).ToList();

    return Results.Ok(cards);
});

// Provider_Standalone's "is my Epic session still good" indicator, kept entirely within this app — no FHIRBridge
// changes needed. FHIRBridge never discloses the raw Epic token to third-party apps, so this deliberately does NOT
// try to hold one either; it just remembers which patient/workflow this HealthApp user last successfully launched
// (from FHIRBridge's own launch-result) and the last time a real fetch through FHIRBridge actually confirmed it
// still worked. That's a "last known good" signal, not a live guarantee — the actual authority remains attempting
// the real /run call, which the frontend already does and falls back gracefully from if this turns out stale.
app.MapPost("/api/epic-session", (EpicSessionRequest request, HttpContext http, SessionStore sessions, EpicSessionStore epicSessions, ProviderStandaloneCallerIdStore providerStandaloneCallerIds) =>
{
    if (!TryGetSession(http, sessions, out var userId, out _))
    {
        return Results.Unauthorized();
    }

    epicSessions.Set(userId, request.PatientId, request.WorkflowId, DateTime.UtcNow);
    // See ProviderStandaloneCallerIdStore's remarks — only the Provider Standalone flow ever sends SessionId
    // (Patient Standalone's own rememberEpicSession call omits it), so this is a no-op for every other role.
    if (!string.IsNullOrWhiteSpace(request.SessionId))
    {
        providerStandaloneCallerIds.Set(request.SessionId);
    }
    return Results.Ok();
});

app.MapGet("/api/epic-session/status", (HttpContext http, SessionStore sessions, EpicSessionStore epicSessions) =>
{
    if (!TryGetSession(http, sessions, out var userId, out _))
    {
        return Results.Unauthorized();
    }

    if (!epicSessions.TryGet(userId, out var patientId, out var workflowId, out var lastConfirmedValidUtc))
    {
        return Results.Ok(new { hasSession = false, patientId = (string?)null, workflowId = (string?)null, lastConfirmedValidUtc = (DateTime?)null });
    }

    return Results.Ok(new { hasSession = true, patientId, workflowId, lastConfirmedValidUtc = (DateTime?)lastConfirmedValidUtc });
});

app.MapDelete("/api/epic-session", (HttpContext http, SessionStore sessions, EpicSessionStore epicSessions) =>
{
    if (!TryGetSession(http, sessions, out var userId, out _))
    {
        return Results.Unauthorized();
    }

    epicSessions.Remove(userId);
    return Results.Ok();
});

app.MapBackendSystemEndpoints();

// "New 11" menu data — read-only GETs over the curated _11 tables, available to any authenticated role.
app.MapResource11Endpoints();

// Data Lake Webhook receiver — the "lake" side of FHIRBridge's DataLakeWebhook destination, so a
// workflow can be run end to end against this app. POST /api/datalake/webhook is anonymous by design
// (server-to-server, no session cookie); see DataLakeWebhookEndpoints for the optional
// DataLakeWebhook:AuthMode credential check.
app.MapDataLakeWebhookEndpoints();

// Four sample third-party APIs (single object / array / envelope / arbitrary partner-shaped event) so
// FHIRBridge's ApiEndpoint destination can be pointed at each one end to end — see ApiEndpointTestEndpoints.
app.MapApiEndpointTestEndpoints();

// SPA fallback: any GET that doesn't match a mapped route or an existing static file resolves to
// index.html instead of 404ing, so Angular's client-side routes work on refresh/deep link. Fallback
// endpoints are always lowest-priority, so this can't shadow the /api/* routes above regardless of
// registration order.
app.MapFallbackToFile("index.html");

app.Run();

// Shared by every settings-reading endpoint (/api/settings, /api/patient-standalone-settings) so each one only
// has to know its own response projection, not repeat the FindAsync(1)-and-default-if-missing lookup. Returns a
// detached, never-null default row rather than modifying the seed data — callers that need to persist changes
// (POST /api/settings) still do their own tracked FindAsync/Add.
static async Task<WorkflowSettingsEntity> GetWorkflowSettingsAsync(HealthAppDbContext db) =>
    await db.WorkflowSettings.FindAsync(1) ?? new WorkflowSettingsEntity { Id = 1 };

// Uniform fallback for any field a record doesn't have — a row written directly by a FHIRBridge SQL
// destination can leave most flattened columns (and even ResourceId/Payload) unset.
const string NotAvailable = "N/A";

static string Na(string? value) => string.IsNullOrWhiteSpace(value) ? NotAvailable : value;

static string NaUtc(DateTime? value) => value?.ToString("yyyy-MM-dd HH:mm:ss 'UTC'") ?? NotAvailable;

static string DisplayDateOfBirth(PatientEntity patient) =>
    TryParseBirthDate(patient, out var birthDate) ? birthDate.ToString("MMMM d, yyyy") : Na(patient.BirthDate);

static string DisplayAge(PatientEntity patient) =>
    TryParseBirthDate(patient, out var birthDate) ? $"{CalculateAge(birthDate)} Years" : NotAvailable;

static bool TryParseBirthDate(PatientEntity patient, out DateOnly birthDate)
{
    birthDate = default;
    return patient.BirthDate is not null && DateOnly.TryParse(patient.BirthDate, out birthDate);
}

static int CalculateAge(DateOnly birthDate)
{
    var today = DateOnly.FromDateTime(DateTime.UtcNow);
    var age = today.Year - birthDate.Year;
    if (today < birthDate.AddYears(age))
    {
        age--;
    }

    return age;
}

static PatientCardDto ToCard(PatientEntity patient) => new(
    ResourceId: Na(patient.ResourceId),
    RecordCreatedOn: NaUtc(patient.RecordCreatedOn),
    FullName: Na(patient.FullName),
    FirstName: Na(patient.FirstName),
    MiddleName: Na(patient.MiddleName),
    LastName: Na(patient.LastName),
    Gender: Na(patient.Gender),
    LegalSex: Na(patient.LegalSex),
    SexForClinicalUse: Na(patient.SexForClinicalUse),
    Pronouns: Na(patient.Pronouns),
    DateOfBirth: DisplayDateOfBirth(patient),
    Age: DisplayAge(patient),
    MaritalStatus: Na(patient.MaritalStatus),
    PatientStatus: Na(patient.PatientStatus),
    Deceased: Na(patient.Deceased));

static bool TryGetSession(HttpContext http, SessionStore sessions, out int userId, out string role)
{
    userId = 0;
    role = string.Empty;
    return http.Request.Cookies.TryGetValue(SessionCookieName, out var sessionId)
        && sessions.TryGet(sessionId, out userId, out _, out role);
}

record LoginRequest(string Email, string Password);
record SaveSettingsRequest(
    string WorkflowUrl,
    string PatientWorkflowId,
    string PatientDetailWorkflowId,
    string PatientBaseUrl,
    string PatientCsvExportWorkflowId,
    string PatientCsvEmailExportWorkflowId,
    string AthenaPatientWorkflowId,
    string AthenaPatientBaseUrl,
    string AthenaEhrEndpointId,
    string EcwPatientWorkflowId,
    string EcwPatientBaseUrl,
    string EcwEhrEndpointId,
    string StandaloneWorkflowId,
    string StandaloneDetailWorkflowId,
    string StandaloneBaseUrl,
    string EcwProviderStandaloneListWorkflowId,
    string EcwProviderStandaloneDetailWorkflowId,
    string EcwProviderStandaloneBaseUrl,
    string EcwProviderStandaloneEhrEndpointId,
    string ProviderInAppWorkflowId,
    string EcwProviderInAppWorkflowId,
    string BackendSystemPractitionerImportWorkflowId);
// Matches FHIRBridge's GET /api/v1/workflows/{id}/public-launch-context response shape.
record MintedLaunchContext(string Context);
// Matches the subset of FHIRBridge's GET /api/v1/workflows/{id}/latest-launch-result this app needs — the run id
// of the workflow's most recent run that produced a Patient (the frontend then binds via the existing
// getPatient(workflowRunId) launch-result path).
record LatestLaunchResult(string? WorkflowRunId);
// The subset of FHIRBridge's GET /api/v1/workflows/runs/{id}/launch-result response this app actually needs —
// the full response also carries the raw Patient resource itself, which /api/account-context-link/check has no
// use for.
record LaunchResultPatientId(string? PatientId);
// PatientId is nullable: the very first OAuth callback often has no specific patient resolved yet (an interactive
// launch's auto-triggered workflow run has no search criteria to work with) — but the Epic session itself is
// already live at that point (saved under FHIRBridge's "default" token slot), so it's still worth remembering.
record EpicSessionRequest(string? PatientId, string WorkflowId, string? SessionId = null);
record ResourceEnvelope(string ResourceType, string ResourceId, string Payload);
record ResourcesEnvelope(List<ResourceEnvelope> Resources);

record PatientCardDto(
    string ResourceId,
    string RecordCreatedOn,
    string? FullName,
    string? FirstName,
    string? MiddleName,
    string? LastName,
    string? Gender,
    string? LegalSex,
    string? SexForClinicalUse,
    string? Pronouns,
    string? DateOfBirth,
    string? Age,
    string? MaritalStatus,
    string? PatientStatus,
    string? Deceased);

sealed class SessionStore
{
    private readonly ConcurrentDictionary<string, (int UserId, string Email, string Role, DateTime ExpiresUtc)> _sessions = new();

    public void Set(string sessionId, int userId, string email, string role, DateTime expiresUtc) =>
        _sessions[sessionId] = (userId, email, role, expiresUtc);

    public bool TryGet(string sessionId, out int userId, out string email, out string role)
    {
        if (_sessions.TryGetValue(sessionId, out var entry) && entry.ExpiresUtc > DateTime.UtcNow)
        {
            userId = entry.UserId;
            email = entry.Email;
            role = entry.Role;
            return true;
        }

        userId = 0;
        email = string.Empty;
        role = string.Empty;
        return false;
    }

    public void Remove(string sessionId) => _sessions.TryRemove(sessionId, out _);
}

/// <summary>
/// In-memory only, keyed by HealthApp userId — mirrors SessionStore's own pattern. Never holds the actual Epic
/// token (FHIRBridge doesn't disclose it to third-party apps); just the last patient/workflow this user launched
/// and when a real fetch through FHIRBridge last confirmed that session still works. Wiped on backend restart —
/// acceptable here since it forces a fresh, honest re-check rather than showing stale confidence.
/// </summary>
sealed class EpicSessionStore
{
    private readonly ConcurrentDictionary<int, (string? PatientId, string WorkflowId, DateTime LastConfirmedValidUtc)> _sessions = new();

    public void Set(int userId, string? patientId, string workflowId, DateTime lastConfirmedValidUtc) =>
        _sessions[userId] = (patientId, workflowId, lastConfirmedValidUtc);

    public bool TryGet(int userId, out string? patientId, out string workflowId, out DateTime lastConfirmedValidUtc)
    {
        if (_sessions.TryGetValue(userId, out var entry))
        {
            patientId = entry.PatientId;
            workflowId = entry.WorkflowId;
            lastConfirmedValidUtc = entry.LastConfirmedValidUtc;
            return true;
        }

        patientId = null;
        workflowId = string.Empty;
        lastConfirmedValidUtc = default;
        return false;
    }

    public void Remove(int userId) => _sessions.TryRemove(userId, out _);
}

/// <summary>
/// In-memory, global (NOT per-user) — the single most recent Provider Standalone sessionId, i.e. the FHIRBridge
/// callerId that role's own interactive OAuth sign-in last authorized a token under (see
/// SmartAuthorizationCodeTokenProvider.BuildStoreKey). Deliberately shared across every role: the Backend System
/// role's own Import Practitioner flow (BackendSystemEndpoints) has no interactive session of its own — by
/// explicit product decision for this demo app, it reuses whichever Provider Standalone token was authorized
/// most recently, rather than needing its own separate sign-in. This is a deliberate, demo-only choice: real
/// production FHIRBridge callers must supply their own genuine callerId (or, for true Backend Services auth,
/// none at all) — see SmartAuthorizationCodeTokenProvider's CallerId-keyed cache and its cross-account-leakage
/// remarks for why a real caller must never do this. Wiped on backend restart, same as EpicSessionStore.
/// </summary>
sealed class ProviderStandaloneCallerIdStore
{
    private volatile string? _sessionId;

    public void Set(string sessionId) => _sessionId = sessionId;

    public string? Get() => _sessionId;
}
