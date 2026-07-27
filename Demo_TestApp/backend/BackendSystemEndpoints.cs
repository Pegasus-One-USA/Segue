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
        // "source" selects which store answers the request — sql (default) / mysql / nosql, driven by the
        // BackendSystem Patient List page's data-source radio group (see backend-system.ts). Every other route in
        // this file deliberately keeps reading HealthAppDbContext/SQL Server unconditionally — only the list and
        // single-patient detail were asked to switch stores.
        app.MapGet("/api/backend-system/patients", async (string? source, HttpContext http, SessionStore sessions, PatientDataSourceResolver dataSources, CancellationToken cancellationToken) =>
        {
            if (!TryGetSession(http, sessions))
            {
                return Results.Unauthorized();
            }

            var patients = await dataSources.Resolve(source).GetPatientsAsync(cancellationToken);
            return Results.Ok(patients);
        });

        app.MapGet("/api/backend-system/patient/{patientId}", async (string patientId, string? source, HttpContext http, SessionStore sessions, PatientDataSourceResolver dataSources, CancellationToken cancellationToken) =>
        {
            if (!TryGetSession(http, sessions))
            {
                return Results.Unauthorized();
            }

            var patient = await dataSources.Resolve(source).GetPatientAsync(patientId, cancellationToken);
            return patient is null ? Results.NotFound() : Results.Ok(patient);
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

            var patientReference = $"Patient/{patientId}";
            var practitionerIds = await db.Encounters
                .Where(e => (e.PatientId == patientId || e.PatientId == patientReference) && e.PractitionerId != null)
                .Select(e => e.PractitionerId!)
                .Distinct()
                .ToListAsync();

            var barePractitionerIds = practitionerIds.Select(StripReferencePrefix).ToList();
            var practitioners = await db.Practitioners
                .Where(p => barePractitionerIds.Contains(p.PractitionerId))
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

            var patientReference = $"Patient/{patientId}";
            var encounters = await db.Encounters
                .Where(e => e.PatientId == patientId || e.PatientId == patientReference)
                .OrderByDescending(e => e.StartDateTime)
                .ToListAsync();

            var result = encounters.Select(e => new EncounterDto(
                e.EncounterId,
                StripReferencePrefix(e.PatientId),
                StripReferencePrefix(e.PractitionerId),
                e.Identifier,
                e.Status,
                e.Class,
                e.Type,
                e.Priority,
                e.StartDateTime,
                e.EndDateTime,
                e.ServiceProvider,
                e.ReasonCode));

            return Results.Ok(result);
        });

        app.MapGet("/api/backend-system/patient/{patientId}/allergy-intolerances", async (string patientId, HttpContext http, SessionStore sessions, HealthAppDbContext db) =>
        {
            if (!TryGetSession(http, sessions))
            {
                return Results.Unauthorized();
            }

            var patientReference = $"Patient/{patientId}";
            var allergies = await db.AllergyIntolerances
                .Where(a => a.PatientId == patientId || a.PatientId == patientReference)
                .OrderByDescending(a => a.RecordedDate)
                .ToListAsync();

            var result = allergies.Select(a => new AllergyIntoleranceDto(
                a.AllergyIntoleranceId,
                StripReferencePrefix(a.PatientId),
                StripReferencePrefix(a.EncounterId),
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
                a.RecordedDate));

            return Results.Ok(result);
        });

        app.MapGet("/api/backend-system/patient/{patientId}/observations", async (string patientId, HttpContext http, SessionStore sessions, HealthAppDbContext db) =>
        {
            if (!TryGetSession(http, sessions))
            {
                return Results.Unauthorized();
            }

            var patientReference = $"Patient/{patientId}";
            var observations = await db.Observations
                .Where(o => o.PatientId == patientId || o.PatientId == patientReference)
                .OrderByDescending(o => o.EffectiveDateTime)
                .ToListAsync();

            var result = observations.Select(o => new ObservationDto(
                o.ObservationId,
                StripReferencePrefix(o.PatientId),
                StripReferencePrefix(o.EncounterId),
                o.Identifier,
                o.Status,
                o.Category,
                o.Code,
                o.Value,
                o.Unit,
                o.Interpretation,
                o.EffectiveDateTime,
                o.IssuedDateTime));

            return Results.Ok(result);
        });

        app.MapGet("/api/backend-system/patient/{patientId}/conditions", async (string patientId, HttpContext http, SessionStore sessions, HealthAppDbContext db) =>
        {
            if (!TryGetSession(http, sessions))
            {
                return Results.Unauthorized();
            }

            var patientReference = $"Patient/{patientId}";
            var conditions = await db.Conditions
                .Where(c => c.PatientId == patientId || c.PatientId == patientReference)
                .OrderByDescending(c => c.RecordedDate)
                .ToListAsync();

            var result = conditions.Select(c => new ConditionDto(
                c.ConditionId,
                StripReferencePrefix(c.PatientId),
                StripReferencePrefix(c.EncounterId),
                c.Identifier,
                c.ClinicalStatus,
                c.VerificationStatus,
                c.Category,
                c.Severity,
                c.Code,
                c.BodySite,
                c.OnsetDateTime,
                c.AbatementDateTime,
                c.RecordedDate));

            return Results.Ok(result);
        });

        app.MapGet("/api/backend-system/patient/{patientId}/procedures", async (string patientId, HttpContext http, SessionStore sessions, HealthAppDbContext db) =>
        {
            if (!TryGetSession(http, sessions))
            {
                return Results.Unauthorized();
            }

            var patientReference = $"Patient/{patientId}";
            var procedures = await db.Procedures
                .Where(p => p.PatientId == patientId || p.PatientId == patientReference)
                .OrderByDescending(p => p.PerformedStartDateTime)
                .ToListAsync();

            var result = procedures.Select(p => new ProcedureDto(
                p.ProcedureId,
                StripReferencePrefix(p.PatientId),
                StripReferencePrefix(p.EncounterId),
                StripReferencePrefix(p.PractitionerId),
                p.Identifier,
                p.Status,
                p.Category,
                p.Code,
                p.BodySite,
                p.Outcome,
                p.PerformedStartDateTime,
                p.PerformedEndDateTime));

            return Results.Ok(result);
        });

        app.MapGet("/api/backend-system/patient/{patientId}/service-requests", async (string patientId, HttpContext http, SessionStore sessions, HealthAppDbContext db) =>
        {
            if (!TryGetSession(http, sessions))
            {
                return Results.Unauthorized();
            }

            var patientReference = $"Patient/{patientId}";
            var serviceRequests = await db.ServiceRequests
                .Where(s => s.PatientId == patientId || s.PatientId == patientReference)
                .OrderByDescending(s => s.AuthoredOn)
                .ToListAsync();

            var result = serviceRequests.Select(s => new ServiceRequestDto(
                s.ServiceRequestId,
                StripReferencePrefix(s.PatientId),
                StripReferencePrefix(s.EncounterId),
                StripReferencePrefix(s.PractitionerId),
                s.Identifier,
                s.Status,
                s.Intent,
                s.Priority,
                s.Category,
                s.Code,
                s.AuthoredOn,
                s.OccurrenceDateTime,
                s.ReasonCode));

            return Results.Ok(result);
        });

        app.MapGet("/api/backend-system/patient/{patientId}/diagnostic-reports", async (string patientId, HttpContext http, SessionStore sessions, HealthAppDbContext db) =>
        {
            if (!TryGetSession(http, sessions))
            {
                return Results.Unauthorized();
            }

            var patientReference = $"Patient/{patientId}";
            var diagnosticReports = await db.DiagnosticReports
                .Where(d => d.PatientId == patientId || d.PatientId == patientReference)
                .OrderByDescending(d => d.IssuedDateTime)
                .ToListAsync();

            var result = diagnosticReports.Select(d => new DiagnosticReportDto(
                d.DiagnosticReportId,
                StripReferencePrefix(d.PatientId),
                StripReferencePrefix(d.EncounterId),
                d.Identifier,
                d.Status,
                d.Category,
                d.Code,
                d.EffectiveDateTime,
                d.IssuedDateTime,
                d.Conclusion));

            return Results.Ok(result);
        });

        app.MapGet("/api/backend-system/patient/{patientId}/medication-requests", async (string patientId, HttpContext http, SessionStore sessions, HealthAppDbContext db) =>
        {
            if (!TryGetSession(http, sessions))
            {
                return Results.Unauthorized();
            }

            var patientReference = $"Patient/{patientId}";
            var medicationRequests = await db.MedicationRequests
                .Where(m => m.PatientId == patientId || m.PatientId == patientReference)
                .OrderByDescending(m => m.AuthoredOn)
                .ToListAsync();

            var result = medicationRequests.Select(m => new MedicationRequestDto(
                m.MedicationRequestId,
                StripReferencePrefix(m.PatientId),
                StripReferencePrefix(m.EncounterId),
                StripReferencePrefix(m.PractitionerId),
                m.Identifier,
                m.Status,
                m.Intent,
                m.Priority,
                m.MedicationCode,
                m.DosageInstruction,
                m.AuthoredOn));

            return Results.Ok(result);
        });

        app.MapGet("/api/backend-system/patient/{patientId}/medication-administrations", async (string patientId, HttpContext http, SessionStore sessions, HealthAppDbContext db) =>
        {
            if (!TryGetSession(http, sessions))
            {
                return Results.Unauthorized();
            }

            var patientReference = $"Patient/{patientId}";
            var medicationAdministrations = await db.MedicationAdministrations
                .Where(m => m.PatientId == patientId || m.PatientId == patientReference)
                .OrderByDescending(m => m.EffectiveDateTime)
                .ToListAsync();

            var result = medicationAdministrations.Select(m => new MedicationAdministrationDto(
                m.MedicationAdministrationId,
                StripReferencePrefix(m.PatientId),
                StripReferencePrefix(m.EncounterId),
                StripReferencePrefix(m.MedicationRequestId),
                m.Identifier,
                m.Status,
                m.MedicationCode,
                m.Dosage,
                m.Route,
                m.EffectiveDateTime,
                m.Note));

            return Results.Ok(result);
        });
    }

    private static bool TryGetSession(HttpContext http, SessionStore sessions) =>
        http.Request.Cookies.TryGetValue(SessionCookieName, out var sessionId)
            && sessions.TryGet(sessionId, out _, out _, out _);

    // Upstream FHIR-mapping pipelines write reference-typed columns (PatientId, EncounterId, PractitionerId,
    // MedicationRequestId) as raw FHIR references (e.g. "Patient/e0w0LEDCYtfckT6N.CkJKCw3") rather than the bare id
    // stored on the referenced row's own primary key — the mapping engine has no reference-parsing step. Strip the
    // "Type/" segment here, at the read boundary, so every id this API returns is bare and consistent regardless of
    // whether the underlying pipeline run happened to record it with or without the prefix.
    private static string? StripReferencePrefix(string? reference) =>
        reference is not null && reference.IndexOf('/') is var slash && slash >= 0
            ? reference[(slash + 1)..]
            : reference;

    private static string? BuildFullName(string? givenName, string? middleName, string? familyName) =>
        BuildFullNamePublic(givenName, middleName, familyName);

    // Shared with the MySQL/Mongo readers (PatientDataSourceReaders.cs) so all three data sources build a
    // patient's display name the exact same way, regardless of which store answered the request.
    public static string? BuildFullNamePublic(string? givenName, string? middleName, string? familyName)
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
