using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace HealthAppBackend;

// Read-only endpoints for the "_11" curated tables, backing the "New 11" menu that every non-Admin role gets, plus
// the New 11 workflow-settings (Admin) and the Practitioner "import missing" flow. Each list route returns EVERY
// row of one _11 table, as-is (null stays null — the Angular side turns a missing value into a display
// placeholder). Session-cookie auth, matching the rest of the app.
public static class Resource11Endpoints
{
    private const string SessionCookieName = "hb_session";

    // The four non-Admin roles that have a "New 11" menu + their own workflow setting (see HealthAppDbContext.cs).
    private static readonly string[] New11Roles =
        [UserRoles.Patient, UserRoles.ProviderStandalone, UserRoles.ProviderInApp, UserRoles.BackendSystem];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void MapResource11Endpoints(this WebApplication app)
    {
        app.MapGet("/api/v11/patients", async (HttpContext http, SessionStore sessions, HealthAppDbContext db, CancellationToken ct) =>
            !TryGetSession(http, sessions)
                ? Results.Unauthorized()
                : Results.Ok(await db.Patients11.AsNoTracking().OrderBy(x => x.FullName).ToListAsync(ct)));

        app.MapGet("/api/v11/practitioners", async (HttpContext http, SessionStore sessions, HealthAppDbContext db, CancellationToken ct) =>
            !TryGetSession(http, sessions)
                ? Results.Unauthorized()
                : Results.Ok(await db.Practitioners11.AsNoTracking().OrderBy(x => x.FullName).ToListAsync(ct)));

        app.MapGet("/api/v11/encounters", async (HttpContext http, SessionStore sessions, HealthAppDbContext db, CancellationToken ct) =>
            !TryGetSession(http, sessions)
                ? Results.Unauthorized()
                : Results.Ok(await db.Encounters11.AsNoTracking().OrderByDescending(x => x.StartDate).ToListAsync(ct)));

        app.MapGet("/api/v11/observations", async (HttpContext http, SessionStore sessions, HealthAppDbContext db, CancellationToken ct) =>
            !TryGetSession(http, sessions)
                ? Results.Unauthorized()
                : Results.Ok(await db.Observations11.AsNoTracking().OrderByDescending(x => x.EffectiveDateTime).ToListAsync(ct)));

        app.MapGet("/api/v11/conditions", async (HttpContext http, SessionStore sessions, HealthAppDbContext db, CancellationToken ct) =>
            !TryGetSession(http, sessions)
                ? Results.Unauthorized()
                : Results.Ok(await db.Conditions11.AsNoTracking().OrderByDescending(x => x.RecordedDate).ToListAsync(ct)));

        app.MapGet("/api/v11/allergy-intolerances", async (HttpContext http, SessionStore sessions, HealthAppDbContext db, CancellationToken ct) =>
            !TryGetSession(http, sessions)
                ? Results.Unauthorized()
                : Results.Ok(await db.AllergyIntolerances11.AsNoTracking().OrderByDescending(x => x.RecordedDate).ToListAsync(ct)));

        app.MapGet("/api/v11/medication-requests", async (HttpContext http, SessionStore sessions, HealthAppDbContext db, CancellationToken ct) =>
            !TryGetSession(http, sessions)
                ? Results.Unauthorized()
                : Results.Ok(await db.MedicationRequests11.AsNoTracking().OrderByDescending(x => x.PrescribedDate).ToListAsync(ct)));

        app.MapGet("/api/v11/medication-administrations", async (HttpContext http, SessionStore sessions, HealthAppDbContext db, CancellationToken ct) =>
            !TryGetSession(http, sessions)
                ? Results.Unauthorized()
                : Results.Ok(await db.MedicationAdministrations11.AsNoTracking().OrderByDescending(x => x.AdministeredDateTime).ToListAsync(ct)));

        app.MapGet("/api/v11/service-requests", async (HttpContext http, SessionStore sessions, HealthAppDbContext db, CancellationToken ct) =>
            !TryGetSession(http, sessions)
                ? Results.Unauthorized()
                : Results.Ok(await db.ServiceRequests11.AsNoTracking().OrderByDescending(x => x.OrderedDate).ToListAsync(ct)));

        app.MapGet("/api/v11/diagnostic-reports", async (HttpContext http, SessionStore sessions, HealthAppDbContext db, CancellationToken ct) =>
            !TryGetSession(http, sessions)
                ? Results.Unauthorized()
                : Results.Ok(await db.DiagnosticReports11.AsNoTracking().OrderByDescending(x => x.IssuedDateTime).ToListAsync(ct)));

        app.MapGet("/api/v11/procedures", async (HttpContext http, SessionStore sessions, HealthAppDbContext db, CancellationToken ct) =>
            !TryGetSession(http, sessions)
                ? Results.Unauthorized()
                : Results.Ok(await db.Procedures11.AsNoTracking().OrderByDescending(x => x.PerformedDate).ToListAsync(ct)));

        // ---- Practitioner "import missing" -------------------------------------------------------------------

        // Practitioners referenced by other _11 tables (Encounter, Observation, ...) that have no row of their own
        // in Practitioner_11 yet. The client's Practitioner tab offers to import these via the role's workflow.
        app.MapGet("/api/v11/practitioners/missing", async (HttpContext http, SessionStore sessions, HealthAppDbContext db, CancellationToken ct) =>
        {
            if (!TryGetSession(http, sessions))
            {
                return Results.Unauthorized();
            }

            return Results.Ok(await ComputeMissingPractitionersAsync(db, ct));
        });

        // Calls the current role's configured New 11 workflow URL, expects a { "Resources": [...] } body of
        // Practitioner resources, and upserts each into Practitioner_11. Which workflow runs depends on the logged-in
        // role, so each role imports from its own configured source.
        app.MapPost("/api/v11/practitioners/import", async (HttpContext http, SessionStore sessions, HealthAppDbContext db, IHttpClientFactory httpClientFactory, CancellationToken ct) =>
        {
            if (!TryGetSessionRole(http, sessions, out var role))
            {
                return Results.Unauthorized();
            }

            var setting = await db.Resource11WorkflowSettings.FirstOrDefaultAsync(s => s.Role == role, ct);
            var workflowUrl = setting?.WorkflowUrl;
            if (string.IsNullOrWhiteSpace(workflowUrl))
            {
                return Results.Ok(new { status = "Failed", errorMessage = $"No New 11 workflow is configured for the {role} role. Ask an admin to set one on the Admin → New 11 tab." });
            }

            var missing = await ComputeMissingPractitionersAsync(db, ct);
            if (missing.Count == 0)
            {
                return Results.Ok(new { status = "Succeeded", imported = 0, message = "No missing practitioners to import." });
            }

            var client = httpClientFactory.CreateClient("Workflow");
            HttpResponseMessage response;
            try
            {
                // Send the missing ids as a hint; a smart workflow can fetch exactly those, a simple one can ignore it.
                var requestBody = JsonSerializer.Serialize(new { practitionerIds = missing.Select(m => m.PractitionerId).ToArray() });
                response = await client.PostAsync(workflowUrl, new StringContent(requestBody, Encoding.UTF8, "application/json"), ct);
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
                var body = await response.Content.ReadAsStringAsync(ct);
                envelope = JsonSerializer.Deserialize<ResourcesEnvelope>(body, JsonOptions);
            }
            catch (JsonException ex)
            {
                return Results.Ok(new { status = "Failed", errorMessage = $"Unexpected workflow response: {ex.Message}" });
            }

            if (envelope?.Resources is null || envelope.Resources.Count == 0)
            {
                return Results.Ok(new { status = "Failed", errorMessage = "Workflow response contained no resources." });
            }

            var imported = 0;
            foreach (var resource in envelope.Resources.Where(r => string.Equals(r.ResourceType, "Practitioner", StringComparison.OrdinalIgnoreCase)))
            {
                PractitionerFields fields;
                try
                {
                    fields = PractitionerFieldExtractor.Extract(resource.Payload);
                }
                catch (JsonException)
                {
                    continue; // skip an unparseable payload rather than failing the whole import
                }

                var id = StripReferencePrefix(!string.IsNullOrWhiteSpace(fields.PractitionerId) ? fields.PractitionerId : resource.ResourceId);
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                var existing = await db.Practitioners11.FirstOrDefaultAsync(p => p.PractitionerId == id, ct);
                if (existing is null)
                {
                    existing = new Practitioner11Entity { PractitionerId = id };
                    db.Practitioners11.Add(existing);
                }

                existing.FullName = fields.FullName;
                existing.FirstName = fields.FirstName;
                existing.LastName = fields.LastName;
                existing.Title = fields.Title;
                existing.Credential = fields.Credential;
                existing.Specialty = fields.Specialty;
                existing.Gender = fields.Gender;
                existing.NPI = fields.NPI;
                existing.Phone = fields.Phone;
                existing.Email = fields.Email;
                existing.AddressLine = fields.AddressLine;
                existing.City = fields.City;
                existing.State = fields.State;
                existing.PostalCode = fields.PostalCode;
                existing.IsActive = fields.IsActive;
                imported++;
            }

            await db.SaveChangesAsync(ct);
            return Results.Ok(new { status = "Succeeded", imported });
        });

        // ---- New 11 workflow settings (Admin only) -----------------------------------------------------------

        app.MapGet("/api/v11/workflow-settings", async (HttpContext http, SessionStore sessions, HealthAppDbContext db, CancellationToken ct) =>
        {
            if (!TryGetSessionRole(http, sessions, out var role))
            {
                return Results.Unauthorized();
            }
            if (role != UserRoles.Admin)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var saved = await db.Resource11WorkflowSettings.AsNoTracking().ToDictionaryAsync(s => s.Role, s => s.WorkflowUrl, ct);
            var all = New11Roles
                .Select(r => new Resource11WorkflowSettingDto(r, saved.TryGetValue(r, out var url) ? url : string.Empty))
                .ToList();
            return Results.Ok(all);
        });

        app.MapPost("/api/v11/workflow-settings", async (List<Resource11WorkflowSettingDto> updates, HttpContext http, SessionStore sessions, HealthAppDbContext db, CancellationToken ct) =>
        {
            if (!TryGetSessionRole(http, sessions, out var role))
            {
                return Results.Unauthorized();
            }
            if (role != UserRoles.Admin)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            foreach (var update in updates ?? [])
            {
                if (update is null || !New11Roles.Contains(update.Role))
                {
                    continue; // ignore unknown roles
                }

                var row = await db.Resource11WorkflowSettings.FirstOrDefaultAsync(s => s.Role == update.Role, ct);
                if (row is null)
                {
                    row = new Resource11WorkflowSettingEntity { Role = update.Role };
                    db.Resource11WorkflowSettings.Add(row);
                }
                row.WorkflowUrl = update.WorkflowUrl?.Trim() ?? string.Empty;
            }

            await db.SaveChangesAsync(ct);

            var saved = await db.Resource11WorkflowSettings.AsNoTracking().ToDictionaryAsync(s => s.Role, s => s.WorkflowUrl, ct);
            var all = New11Roles
                .Select(r => new Resource11WorkflowSettingDto(r, saved.TryGetValue(r, out var url) ? url : string.Empty))
                .ToList();
            return Results.Ok(all);
        });
    }

    // Distinct practitioner ids referenced across every _11 table EXCEPT Practitioner_11, minus those already
    // present in Practitioner_11. Name is taken from whichever referencing row first carries one.
    private static async Task<List<PractitionerRefDto>> ComputeMissingPractitionersAsync(HealthAppDbContext db, CancellationToken ct)
    {
        var refs = new List<(string? Id, string? Name)>();
        refs.AddRange((await db.Patients11.Where(x => x.PrimaryCareProviderId != null).Select(x => new { x.PrimaryCareProviderId, x.PrimaryCareProviderName }).ToListAsync(ct)).Select(x => (x.PrimaryCareProviderId, x.PrimaryCareProviderName)));
        refs.AddRange((await db.Encounters11.Where(x => x.AttendingProviderId != null).Select(x => new { x.AttendingProviderId, x.AttendingProviderName }).ToListAsync(ct)).Select(x => (x.AttendingProviderId, x.AttendingProviderName)));
        refs.AddRange((await db.Observations11.Where(x => x.PerformedById != null).Select(x => new { x.PerformedById, x.PerformedByName }).ToListAsync(ct)).Select(x => (x.PerformedById, x.PerformedByName)));
        refs.AddRange((await db.Conditions11.Where(x => x.RecordedById != null).Select(x => new { x.RecordedById, x.RecordedByName }).ToListAsync(ct)).Select(x => (x.RecordedById, x.RecordedByName)));
        refs.AddRange((await db.MedicationRequests11.Where(x => x.PrescriberId != null).Select(x => new { x.PrescriberId, x.PrescriberName }).ToListAsync(ct)).Select(x => (x.PrescriberId, x.PrescriberName)));
        refs.AddRange((await db.MedicationAdministrations11.Where(x => x.AdministeredById != null).Select(x => new { x.AdministeredById, x.AdministeredByName }).ToListAsync(ct)).Select(x => (x.AdministeredById, x.AdministeredByName)));
        refs.AddRange((await db.ServiceRequests11.Where(x => x.OrderedById != null).Select(x => new { x.OrderedById, x.OrderedByName }).ToListAsync(ct)).Select(x => (x.OrderedById, x.OrderedByName)));
        refs.AddRange((await db.DiagnosticReports11.Where(x => x.PerformedById != null).Select(x => new { x.PerformedById, x.PerformedByName }).ToListAsync(ct)).Select(x => (x.PerformedById, x.PerformedByName)));
        refs.AddRange((await db.Procedures11.Where(x => x.PerformedById != null).Select(x => new { x.PerformedById, x.PerformedByName }).ToListAsync(ct)).Select(x => (x.PerformedById, x.PerformedByName)));

        var existing = (await db.Practitioners11.Select(p => p.PractitionerId).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return refs
            .Select(r => (Id: StripReferencePrefix(r.Id), r.Name))
            .Where(r => !string.IsNullOrWhiteSpace(r.Id) && !existing.Contains(r.Id!))
            .GroupBy(r => r.Id!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new PractitionerRefDto(g.Key, g.Select(x => x.Name).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))))
            .OrderBy(r => r.Name ?? r.PractitionerId)
            .ToList();
    }

    // Reference-typed columns may arrive as bare ids ("eM5C...") or full FHIR references ("Practitioner/eM5C...");
    // normalize to the bare id so it matches Practitioner_11's own key.
    private static string? StripReferencePrefix(string? reference) =>
        reference is not null && reference.IndexOf('/') is var slash && slash >= 0
            ? reference[(slash + 1)..]
            : reference;

    private static bool TryGetSession(HttpContext http, SessionStore sessions) =>
        http.Request.Cookies.TryGetValue(SessionCookieName, out var sessionId)
            && sessions.TryGet(sessionId, out _, out _, out _);

    private static bool TryGetSessionRole(HttpContext http, SessionStore sessions, out string role)
    {
        role = string.Empty;
        if (http.Request.Cookies.TryGetValue(SessionCookieName, out var sessionId)
            && sessions.TryGet(sessionId, out _, out _, out var sessionRole))
        {
            role = sessionRole;
            return true;
        }
        return false;
    }
}

record Resource11WorkflowSettingDto(string Role, string WorkflowUrl);
record PractitionerRefDto(string PractitionerId, string? Name);
