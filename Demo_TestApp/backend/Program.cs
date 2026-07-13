using System.Collections.Concurrent;
using System.Text.Json;
using HealthAppBackend;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var allowedFrontendOrigin = builder.Configuration["AllowedFrontendOrigin"] ?? "http://localhost:5501";
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? "Server=localhost,1433;Database=HealthAppDb;User Id=sa;Password=Your_password123;TrustServerCertificate=True";

builder.Services.AddDbContext<HealthAppDbContext>(options => options.UseSqlServer(connectionString));
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddHttpClient("Workflow");
builder.Services.AddCors(options => options.AddPolicy("Frontend", policy => policy
    .WithOrigins(allowedFrontendOrigin)
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<HealthAppDbContext>();
    db.Database.EnsureCreated();
}

app.UseCors("Frontend");

const string SessionCookieName = "hb_session";
var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

app.MapGet("/", () => "HealthApp backend is running.");

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

    var settings = await db.WorkflowSettings.FindAsync(1);
    return Results.Ok(new { workflowUrl = settings?.WorkflowUrl ?? string.Empty });
});

// Read-only, any authenticated role — lets the Patient's "Process" button redirect the browser to the
// admin-configured workflow URL without granting Patient access to the full (Admin-only) settings endpoint.
app.MapGet("/api/workflow-url", async (HttpContext http, SessionStore sessions, HealthAppDbContext db) =>
{
    if (!TryGetSession(http, sessions, out _, out _))
    {
        return Results.Unauthorized();
    }

    var settings = await db.WorkflowSettings.FindAsync(1);
    return Results.Ok(new { workflowUrl = settings?.WorkflowUrl ?? string.Empty });
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

    settings.WorkflowUrl = request.WorkflowUrl.Trim();
    await db.SaveChangesAsync();

    return Results.Ok(new { workflowUrl = settings.WorkflowUrl });
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

app.Run();

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
record SaveSettingsRequest(string WorkflowUrl);
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
