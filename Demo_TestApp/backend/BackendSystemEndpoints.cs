using Microsoft.EntityFrameworkCore;

namespace HealthAppBackend;

// Read-only patient-centric APIs for the BackendSystem role — kept in its own file (rather than growing
// Program.cs further) but registered as Minimal API routes via MapBackendSystemEndpoints, matching every other
// endpoint's session-cookie auth convention. All fields pass through as-is (null stays null, never coerced to
// "N/A" server-side) — the Angular side owns turning a missing value into a display placeholder (requirement
// section 12). Collections always resolve to an empty list, never null, and every route returns 200 OK even
// when the patient has zero related records for that resource type.
public static class BackendSystemEndpoints
{
    private const string SessionCookieName = "hb_session";

    public static void MapBackendSystemEndpoints(this WebApplication app)
    {
        app.MapGet("/api/backend-system/patients", async (HttpContext http, SessionStore sessions, HealthAppDbContext db) =>
        {
            if (!TryGetSession(http, sessions))
            {
                return Results.Unauthorized();
            }

            var patients = await db.BackendSystemPatients
                .OrderBy(p => p.FamilyName)
                .ThenBy(p => p.GivenName)
                .Select(p => new PatientListItemDto(
                    p.PatientId,
                    BuildFullName(p.GivenName, p.MiddleName, p.FamilyName),
                    p.MRN,
                    p.Identifier,
                    p.Gender,
                    p.BirthDate))
                .ToListAsync();

            return Results.Ok(patients);
        });

        app.MapGet("/api/backend-system/patient/{patientId}", async (string patientId, HttpContext http, SessionStore sessions, HealthAppDbContext db) =>
        {
            if (!TryGetSession(http, sessions))
            {
                return Results.Unauthorized();
            }

            var patient = await db.BackendSystemPatients.FirstOrDefaultAsync(p => p.PatientId == patientId);
            if (patient is null)
            {
                return Results.NotFound();
            }

            return Results.Ok(new PatientDetailDto(
                patient.PatientId,
                patient.Identifier,
                patient.MRN,
                patient.FamilyName,
                patient.GivenName,
                patient.MiddleName,
                BuildFullName(patient.GivenName, patient.MiddleName, patient.FamilyName),
                patient.Gender,
                patient.BirthDate,
                patient.Deceased,
                patient.MaritalStatus,
                patient.Phone,
                patient.Email,
                patient.AddressLine1,
                patient.AddressLine2,
                patient.City,
                patient.State,
                patient.PostalCode,
                patient.Country));
        });

        // Practitioner has no PatientId of its own — the only link to a patient is via the Encounters the
        // practitioner participated in, so this resolves distinct practitioner ids from that patient's encounters
        // first, then looks those up. Every other resource endpoint below filters directly on its own PatientId.
        app.MapGet("/api/backend-system/patient/{patientId}/practitioners", async (string patientId, HttpContext http, SessionStore sessions, HealthAppDbContext db) =>
        {
            if (!TryGetSession(http, sessions))
            {
                return Results.Unauthorized();
            }

            var practitionerIds = await db.Encounters
                .Where(e => e.PatientId == patientId && e.PractitionerId != null)
                .Select(e => e.PractitionerId!)
                .Distinct()
                .ToListAsync();

            var practitioners = await db.Practitioners
                .Where(p => practitionerIds.Contains(p.PractitionerId))
                .OrderBy(p => p.FamilyName)
                .Select(p => new PractitionerDto(
                    p.PractitionerId,
                    p.Identifier,
                    p.NPI,
                    BuildFullName(p.GivenName, p.MiddleName, p.FamilyName),
                    p.Gender,
                    p.Qualification,
                    p.Phone,
                    p.Email))
                .ToListAsync();

            return Results.Ok(practitioners);
        });

        app.MapGet("/api/backend-system/patient/{patientId}/encounters", async (string patientId, HttpContext http, SessionStore sessions, HealthAppDbContext db) =>
        {
            if (!TryGetSession(http, sessions))
            {
                return Results.Unauthorized();
            }

            var encounters = await db.Encounters
                .Where(e => e.PatientId == patientId)
                .OrderByDescending(e => e.StartDateTime)
                .Select(e => new EncounterDto(
                    e.EncounterId,
                    e.PatientId,
                    e.PractitionerId,
                    e.Identifier,
                    e.Status,
                    e.Class,
                    e.Type,
                    e.Priority,
                    e.StartDateTime,
                    e.EndDateTime,
                    e.ServiceProvider,
                    e.ReasonCode))
                .ToListAsync();

            return Results.Ok(encounters);
        });

        app.MapGet("/api/backend-system/patient/{patientId}/allergy-intolerances", async (string patientId, HttpContext http, SessionStore sessions, HealthAppDbContext db) =>
        {
            if (!TryGetSession(http, sessions))
            {
                return Results.Unauthorized();
            }

            var allergies = await db.AllergyIntolerances
                .Where(a => a.PatientId == patientId)
                .OrderByDescending(a => a.RecordedDate)
                .Select(a => new AllergyIntoleranceDto(
                    a.AllergyIntoleranceId,
                    a.PatientId,
                    a.EncounterId,
                    a.Identifier,
                    a.ClinicalStatus,
                    a.VerificationStatus,
                    a.Category,
                    a.Criticality,
                    a.Code,
                    a.Substance,
                    a.Reaction,
                    a.Severity,
                    a.OnsetDateTime,
                    a.RecordedDate))
                .ToListAsync();

            return Results.Ok(allergies);
        });

        app.MapGet("/api/backend-system/patient/{patientId}/observations", async (string patientId, HttpContext http, SessionStore sessions, HealthAppDbContext db) =>
        {
            if (!TryGetSession(http, sessions))
            {
                return Results.Unauthorized();
            }

            var observations = await db.Observations
                .Where(o => o.PatientId == patientId)
                .OrderByDescending(o => o.EffectiveDateTime)
                .Select(o => new ObservationDto(
                    o.ObservationId,
                    o.PatientId,
                    o.EncounterId,
                    o.Identifier,
                    o.Status,
                    o.Category,
                    o.Code,
                    o.Value,
                    o.Unit,
                    o.Interpretation,
                    o.EffectiveDateTime,
                    o.IssuedDateTime))
                .ToListAsync();

            return Results.Ok(observations);
        });

        app.MapGet("/api/backend-system/patient/{patientId}/conditions", async (string patientId, HttpContext http, SessionStore sessions, HealthAppDbContext db) =>
        {
            if (!TryGetSession(http, sessions))
            {
                return Results.Unauthorized();
            }

            var conditions = await db.Conditions
                .Where(c => c.PatientId == patientId)
                .OrderByDescending(c => c.RecordedDate)
                .Select(c => new ConditionDto(
                    c.ConditionId,
                    c.PatientId,
                    c.EncounterId,
                    c.Identifier,
                    c.ClinicalStatus,
                    c.VerificationStatus,
                    c.Category,
                    c.Severity,
                    c.Code,
                    c.BodySite,
                    c.OnsetDateTime,
                    c.AbatementDateTime,
                    c.RecordedDate))
                .ToListAsync();

            return Results.Ok(conditions);
        });

        app.MapGet("/api/backend-system/patient/{patientId}/procedures", async (string patientId, HttpContext http, SessionStore sessions, HealthAppDbContext db) =>
        {
            if (!TryGetSession(http, sessions))
            {
                return Results.Unauthorized();
            }

            var procedures = await db.Procedures
                .Where(p => p.PatientId == patientId)
                .OrderByDescending(p => p.PerformedStartDateTime)
                .Select(p => new ProcedureDto(
                    p.ProcedureId,
                    p.PatientId,
                    p.EncounterId,
                    p.PractitionerId,
                    p.Identifier,
                    p.Status,
                    p.Category,
                    p.Code,
                    p.BodySite,
                    p.Outcome,
                    p.PerformedStartDateTime,
                    p.PerformedEndDateTime))
                .ToListAsync();

            return Results.Ok(procedures);
        });

        app.MapGet("/api/backend-system/patient/{patientId}/service-requests", async (string patientId, HttpContext http, SessionStore sessions, HealthAppDbContext db) =>
        {
            if (!TryGetSession(http, sessions))
            {
                return Results.Unauthorized();
            }

            var serviceRequests = await db.ServiceRequests
                .Where(s => s.PatientId == patientId)
                .OrderByDescending(s => s.AuthoredOn)
                .Select(s => new ServiceRequestDto(
                    s.ServiceRequestId,
                    s.PatientId,
                    s.EncounterId,
                    s.PractitionerId,
                    s.Identifier,
                    s.Status,
                    s.Intent,
                    s.Priority,
                    s.Category,
                    s.Code,
                    s.AuthoredOn,
                    s.OccurrenceDateTime,
                    s.ReasonCode))
                .ToListAsync();

            return Results.Ok(serviceRequests);
        });

        app.MapGet("/api/backend-system/patient/{patientId}/diagnostic-reports", async (string patientId, HttpContext http, SessionStore sessions, HealthAppDbContext db) =>
        {
            if (!TryGetSession(http, sessions))
            {
                return Results.Unauthorized();
            }

            var diagnosticReports = await db.DiagnosticReports
                .Where(d => d.PatientId == patientId)
                .OrderByDescending(d => d.IssuedDateTime)
                .Select(d => new DiagnosticReportDto(
                    d.DiagnosticReportId,
                    d.PatientId,
                    d.EncounterId,
                    d.Identifier,
                    d.Status,
                    d.Category,
                    d.Code,
                    d.EffectiveDateTime,
                    d.IssuedDateTime,
                    d.Conclusion))
                .ToListAsync();

            return Results.Ok(diagnosticReports);
        });

        app.MapGet("/api/backend-system/patient/{patientId}/medication-requests", async (string patientId, HttpContext http, SessionStore sessions, HealthAppDbContext db) =>
        {
            if (!TryGetSession(http, sessions))
            {
                return Results.Unauthorized();
            }

            var medicationRequests = await db.MedicationRequests
                .Where(m => m.PatientId == patientId)
                .OrderByDescending(m => m.AuthoredOn)
                .Select(m => new MedicationRequestDto(
                    m.MedicationRequestId,
                    m.PatientId,
                    m.EncounterId,
                    m.PractitionerId,
                    m.Identifier,
                    m.Status,
                    m.Intent,
                    m.Priority,
                    m.MedicationCode,
                    m.DosageInstruction,
                    m.AuthoredOn))
                .ToListAsync();

            return Results.Ok(medicationRequests);
        });

        app.MapGet("/api/backend-system/patient/{patientId}/medication-administrations", async (string patientId, HttpContext http, SessionStore sessions, HealthAppDbContext db) =>
        {
            if (!TryGetSession(http, sessions))
            {
                return Results.Unauthorized();
            }

            var medicationAdministrations = await db.MedicationAdministrations
                .Where(m => m.PatientId == patientId)
                .OrderByDescending(m => m.EffectiveDateTime)
                .Select(m => new MedicationAdministrationDto(
                    m.MedicationAdministrationId,
                    m.PatientId,
                    m.EncounterId,
                    m.MedicationRequestId,
                    m.Identifier,
                    m.Status,
                    m.MedicationCode,
                    m.Dosage,
                    m.Route,
                    m.EffectiveDateTime,
                    m.Note))
                .ToListAsync();

            return Results.Ok(medicationAdministrations);
        });
    }

    private static bool TryGetSession(HttpContext http, SessionStore sessions) =>
        http.Request.Cookies.TryGetValue(SessionCookieName, out var sessionId)
            && sessions.TryGet(sessionId, out _, out _, out _);

    private static string? BuildFullName(string? givenName, string? middleName, string? familyName)
    {
        var parts = new[] { givenName, middleName, familyName }
            .Where(part => !string.IsNullOrWhiteSpace(part));
        var joined = string.Join(' ', parts);
        return string.IsNullOrWhiteSpace(joined) ? null : joined;
    }
}

record PatientListItemDto(
    string PatientId,
    string? FullName,
    string? MRN,
    string? Identifier,
    string? Gender,
    DateOnly? BirthDate);

record PatientDetailDto(
    string PatientId,
    string? Identifier,
    string? MRN,
    string? FamilyName,
    string? GivenName,
    string? MiddleName,
    string? FullName,
    string? Gender,
    DateOnly? BirthDate,
    bool? Deceased,
    string? MaritalStatus,
    string? Phone,
    string? Email,
    string? AddressLine1,
    string? AddressLine2,
    string? City,
    string? State,
    string? PostalCode,
    string? Country);

record PractitionerDto(
    string PractitionerId,
    string? Identifier,
    string? NPI,
    string? FullName,
    string? Gender,
    string? Qualification,
    string? Phone,
    string? Email);

record EncounterDto(
    string EncounterId,
    string? PatientId,
    string? PractitionerId,
    string? Identifier,
    string? Status,
    string? Class,
    string? Type,
    string? Priority,
    DateTime? StartDateTime,
    DateTime? EndDateTime,
    string? ServiceProvider,
    string? ReasonCode);

record AllergyIntoleranceDto(
    string AllergyIntoleranceId,
    string? PatientId,
    string? EncounterId,
    string? Identifier,
    string? ClinicalStatus,
    string? VerificationStatus,
    string? Category,
    string? Criticality,
    string? Code,
    string? Substance,
    string? Reaction,
    string? Severity,
    DateTime? OnsetDateTime,
    DateTime? RecordedDate);

record ObservationDto(
    string ObservationId,
    string? PatientId,
    string? EncounterId,
    string? Identifier,
    string? Status,
    string? Category,
    string? Code,
    string? Value,
    string? Unit,
    string? Interpretation,
    DateTime? EffectiveDateTime,
    DateTime? IssuedDateTime);

record ConditionDto(
    string ConditionId,
    string? PatientId,
    string? EncounterId,
    string? Identifier,
    string? ClinicalStatus,
    string? VerificationStatus,
    string? Category,
    string? Severity,
    string? Code,
    string? BodySite,
    DateTime? OnsetDateTime,
    DateTime? AbatementDateTime,
    DateTime? RecordedDate);

record ProcedureDto(
    string ProcedureId,
    string? PatientId,
    string? EncounterId,
    string? PractitionerId,
    string? Identifier,
    string? Status,
    string? Category,
    string? Code,
    string? BodySite,
    string? Outcome,
    DateTime? PerformedStartDateTime,
    DateTime? PerformedEndDateTime);

record ServiceRequestDto(
    string ServiceRequestId,
    string? PatientId,
    string? EncounterId,
    string? PractitionerId,
    string? Identifier,
    string? Status,
    string? Intent,
    string? Priority,
    string? Category,
    string? Code,
    DateTime? AuthoredOn,
    DateTime? OccurrenceDateTime,
    string? ReasonCode);

record DiagnosticReportDto(
    string DiagnosticReportId,
    string? PatientId,
    string? EncounterId,
    string? Identifier,
    string? Status,
    string? Category,
    string? Code,
    DateTime? EffectiveDateTime,
    DateTime? IssuedDateTime,
    string? Conclusion);

record MedicationRequestDto(
    string MedicationRequestId,
    string? PatientId,
    string? EncounterId,
    string? PractitionerId,
    string? Identifier,
    string? Status,
    string? Intent,
    string? Priority,
    string? MedicationCode,
    string? DosageInstruction,
    DateTime? AuthoredOn);

record MedicationAdministrationDto(
    string MedicationAdministrationId,
    string? PatientId,
    string? EncounterId,
    string? MedicationRequestId,
    string? Identifier,
    string? Status,
    string? MedicationCode,
    string? Dosage,
    string? Route,
    DateTime? EffectiveDateTime,
    string? Note);
