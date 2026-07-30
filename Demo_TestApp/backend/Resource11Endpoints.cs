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

    // The four non-Admin roles configurable on the Admin → New 11 settings tab, each with a List + Details URL.
    // ("Provider" in the DB column names = ProviderStandalone.)
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

        // ---- Per-patient detail (New 11 Patient List -> patient -> tabs) --------------------------------------

        // Rows of one clinical resource for a single patient. Backs the per-patient tabs shown after a Patient List
        // row is clicked. Reference columns may be bare ids or "Patient/{id}", so match both.
        app.MapGet("/api/v11/patient/{patientId}/{resource}", async (string patientId, string resource, HttpContext http, SessionStore sessions, HealthAppDbContext db, CancellationToken ct) =>
        {
            if (!TryGetSession(http, sessions))
            {
                return Results.Unauthorized();
            }

            var reference = $"Patient/{patientId}";
            return resource switch
            {
                "encounters" => Results.Ok(await db.Encounters11.AsNoTracking().Where(x => x.PatientId == patientId || x.PatientId == reference).OrderByDescending(x => x.StartDate).ToListAsync(ct)),
                "observations" => Results.Ok(await db.Observations11.AsNoTracking().Where(x => x.PatientId == patientId || x.PatientId == reference).OrderByDescending(x => x.EffectiveDateTime).ToListAsync(ct)),
                "conditions" => Results.Ok(await db.Conditions11.AsNoTracking().Where(x => x.PatientId == patientId || x.PatientId == reference).OrderByDescending(x => x.RecordedDate).ToListAsync(ct)),
                "allergy-intolerances" => Results.Ok(await db.AllergyIntolerances11.AsNoTracking().Where(x => x.PatientId == patientId || x.PatientId == reference).OrderByDescending(x => x.RecordedDate).ToListAsync(ct)),
                "medication-requests" => Results.Ok(await db.MedicationRequests11.AsNoTracking().Where(x => x.PatientId == patientId || x.PatientId == reference).OrderByDescending(x => x.PrescribedDate).ToListAsync(ct)),
                "medication-administrations" => Results.Ok(await db.MedicationAdministrations11.AsNoTracking().Where(x => x.PatientId == patientId || x.PatientId == reference).OrderByDescending(x => x.AdministeredDateTime).ToListAsync(ct)),
                "service-requests" => Results.Ok(await db.ServiceRequests11.AsNoTracking().Where(x => x.PatientId == patientId || x.PatientId == reference).OrderByDescending(x => x.OrderedDate).ToListAsync(ct)),
                "diagnostic-reports" => Results.Ok(await db.DiagnosticReports11.AsNoTracking().Where(x => x.PatientId == patientId || x.PatientId == reference).OrderByDescending(x => x.IssuedDateTime).ToListAsync(ct)),
                "procedures" => Results.Ok(await db.Procedures11.AsNoTracking().Where(x => x.PatientId == patientId || x.PatientId == reference).OrderByDescending(x => x.PerformedDate).ToListAsync(ct)),
                _ => Results.NotFound(),
            };
        });

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

            var missing = await ComputeMissingPractitionersAsync(db, ct);
            if (missing.Count == 0)
            {
                return Results.Ok(new { status = "Succeeded", imported = 0, message = "No missing practitioners to import." });
            }

            // Importing specific practitioners by id is a "details" fetch — use the role's Details workflow, falling
            // back to its List workflow when Details is unset.
            var settings = await GetOrCreateSettingsAsync(db, ct);
            var (listUrl, detailsUrl) = GetForRole(settings, role);
            var workflowUrl = !string.IsNullOrWhiteSpace(detailsUrl) ? detailsUrl : listUrl;
            if (string.IsNullOrWhiteSpace(workflowUrl))
            {
                return Results.Ok(new { status = "Failed", errorMessage = $"No New 11 workflow is configured for the {role} role. Ask an admin to set its List/Details URL on the Admin → New 11 tab." });
            }

            var idsCsv = string.Join(",", missing.Select(m => m.PractitionerId));

            var client = httpClientFactory.CreateClient("Workflow");
            HttpResponseMessage response;
            try
            {
                // Pass the missing practitioner ids comma-separated (query string) AND as an array (JSON body) so
                // the workflow can pull exactly those from the source (Epic).
                var separator = workflowUrl.Contains('?') ? "&" : "?";
                var requestUrl = $"{workflowUrl}{separator}practitionerIds={Uri.EscapeDataString(idsCsv)}";
                var requestBody = JsonSerializer.Serialize(new { practitionerIds = missing.Select(m => m.PractitionerId).ToArray() });
                response = await client.PostAsync(requestUrl, new StringContent(requestBody, Encoding.UTF8, "application/json"), ct);
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

            var settings = await GetOrCreateSettingsAsync(db, ct);
            return Results.Ok(ToDtos(settings));
        });

        app.MapPost("/api/v11/workflow-settings", async (List<Resource11RoleWorkflowsDto> updates, HttpContext http, SessionStore sessions, HealthAppDbContext db, CancellationToken ct) =>
        {
            if (!TryGetSessionRole(http, sessions, out var role))
            {
                return Results.Unauthorized();
            }
            if (role != UserRoles.Admin)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var settings = await GetOrCreateSettingsAsync(db, ct);
            foreach (var update in updates ?? [])
            {
                if (update is null || !New11Roles.Contains(update.Role))
                {
                    continue; // ignore unknown roles
                }
                SetForRole(settings, update.Role, update.ListUrl?.Trim() ?? string.Empty, update.DetailsUrl?.Trim() ?? string.Empty);
            }

            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDtos(settings));
        });
    }

    private static async Task<Resource11WorkflowSettingsEntity> GetOrCreateSettingsAsync(HealthAppDbContext db, CancellationToken ct)
    {
        var row = await db.Resource11WorkflowSettings.FirstOrDefaultAsync(s => s.Id == 1, ct);
        if (row is null)
        {
            row = new Resource11WorkflowSettingsEntity { Id = 1 };
            db.Resource11WorkflowSettings.Add(row);
        }
        return row;
    }

    private static List<Resource11RoleWorkflowsDto> ToDtos(Resource11WorkflowSettingsEntity s) =>
        New11Roles.Select(r =>
        {
            var (list, details) = GetForRole(s, r);
            return new Resource11RoleWorkflowsDto(r, list, details);
        }).ToList();

    private static (string List, string Details) GetForRole(Resource11WorkflowSettingsEntity s, string role) => role switch
    {
        UserRoles.Patient => (s.PatientListWorkflowUrl, s.PatientDetailsWorkflowUrl),
        UserRoles.ProviderStandalone => (s.ProviderListWorkflowUrl, s.ProviderDetailsWorkflowUrl),
        UserRoles.ProviderInApp => (s.ProviderInAppListWorkflowUrl, s.ProviderInAppDetailsWorkflowUrl),
        UserRoles.BackendSystem => (s.BackendSystemListWorkflowUrl, s.BackendSystemDetailsWorkflowUrl),
        _ => (string.Empty, string.Empty),
    };

    private static void SetForRole(Resource11WorkflowSettingsEntity s, string role, string list, string details)
    {
        switch (role)
        {
            case UserRoles.Patient: s.PatientListWorkflowUrl = list; s.PatientDetailsWorkflowUrl = details; break;
            case UserRoles.ProviderStandalone: s.ProviderListWorkflowUrl = list; s.ProviderDetailsWorkflowUrl = details; break;
            case UserRoles.ProviderInApp: s.ProviderInAppListWorkflowUrl = list; s.ProviderInAppDetailsWorkflowUrl = details; break;
            case UserRoles.BackendSystem: s.BackendSystemListWorkflowUrl = list; s.BackendSystemDetailsWorkflowUrl = details; break;
        }
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

record Resource11RoleWorkflowsDto(string Role, string ListUrl, string DetailsUrl);
record PractitionerRefDto(string PractitionerId, string? Name);
