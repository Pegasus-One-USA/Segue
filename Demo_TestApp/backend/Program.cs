using System.Collections.Concurrent;
using System.Text.Json;
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

// No-ops unless actually launched by that OS's service manager — lets the same published
// output run as a systemd service on Linux or a Windows Service, with `dotnet run` unaffected.
builder.Host.UseWindowsService().UseSystemd();

var allowedFrontendOrigin = builder.Configuration["AllowedFrontendOrigin"] ?? "http://localhost:5501";
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? "Server=localhost,1433;Database=HealthAppDb;User Id=sa;Password=Your_password123;TrustServerCertificate=True";

builder.Services.AddDbContext<HealthAppDbContext>(options => options.UseSqlServer(connectionString));
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<EpicSessionStore>();
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
}

// Serves the Angular build copied into wwwroot/ at deploy time — this backend hosts its own
// frontend (same origin), so the app's hb_session cookie (SameSite=Lax) works without any
// cross-origin complications.
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseCors("Frontend");

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

const string SessionCookieName = "hb_session";
var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

// Public — populates the "Login Type" dropdown on the login screen, before any session exists.
app.MapGet("/api/demo-types", async (HealthAppDbContext db) =>
{
    var demoTypes = await db.DemoTypes
        .OrderBy(t => t.Id)
        .Select(t => new { t.Id, t.Name })
        .ToListAsync();

    return Results.Ok(demoTypes);
});

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
        patientBaseUrl = settings.PatientBaseUrl
    });
});

// Read-only, any authenticated role — lets PatientStandaloneLaunchService resolve its FHIRBridge workflow ids +
// base URL from the admin-configured settings (formerly a gitignored per-developer local file) without granting
// Patient access to the full Admin settings endpoint. Two distinct workflow ids come back: one for the patient
// list fetch, one for the per-patient detail fetch — each is its own independent FHIRBridge public-launch opt-in.
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
        baseUrl = settings.PatientBaseUrl
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
    await db.SaveChangesAsync();

    return Results.Ok(new
    {
        workflowUrl = settings.WorkflowUrl,
        patientWorkflowId = settings.PatientWorkflowId,
        patientDetailWorkflowId = settings.PatientDetailWorkflowId,
        patientBaseUrl = settings.PatientBaseUrl
    });
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
app.MapPost("/api/epic-session", (EpicSessionRequest request, HttpContext http, SessionStore sessions, EpicSessionStore epicSessions) =>
{
    if (!TryGetSession(http, sessions, out var userId, out _))
    {
        return Results.Unauthorized();
    }

    epicSessions.Set(userId, request.PatientId, request.WorkflowId, DateTime.UtcNow);
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
record SaveSettingsRequest(string WorkflowUrl, string PatientWorkflowId, string PatientDetailWorkflowId, string PatientBaseUrl);
// PatientId is nullable: the very first OAuth callback often has no specific patient resolved yet (an interactive
// launch's auto-triggered workflow run has no search criteria to work with) — but the Epic session itself is
// already live at that point (saved under FHIRBridge's "default" token slot), so it's still worth remembering.
record EpicSessionRequest(string? PatientId, string WorkflowId);
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
