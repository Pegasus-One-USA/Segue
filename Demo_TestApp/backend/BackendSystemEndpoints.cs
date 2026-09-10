using System.Text;
using System.Text.Json;
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

    // FHIRBridge returns camelCase JSON; match it case-insensitively when deserializing a /run response.
    private static readonly JsonSerializerOptions WorkflowJsonOptions = new() { PropertyNameCaseInsensitive = true };

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

        // ---- Practitioners: global list + referenced-id discovery + import ------------------------------------
        // Replaces the old per-patient practitioners tab (removed from the Patient Details left nav). The Practitioners
        // view on the BackendSystem Default screen is a global list plus an "Import Practitioner" flow, not a
        // per-patient sub-resource.

        // Every practitioner already stored in HealthDB's own Practitioner table — backs the Practitioners view's table.
        app.MapGet("/api/backend-system/practitioners", async (HttpContext http, SessionStore sessions, HealthAppDbContext db, CancellationToken cancellationToken) =>
        {
            if (!TryGetSession(http, sessions))
            {
                return Results.Unauthorized();
            }

            var practitioners = await db.Practitioners
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
                .ToListAsync(cancellationToken);

            return Results.Ok(practitioners);
        });

        // Every distinct practitioner id referenced by the Default clinical tables (Encounter, Procedure,
        // ServiceRequest, MedicationRequest), each flagged with whether it already has a row in the Practitioner
        // table. The Practitioners view pre-fills these into its "Import Practitioner" input.
        app.MapGet("/api/backend-system/practitioners/referenced-ids", async (HttpContext http, SessionStore sessions, HealthAppDbContext db, CancellationToken cancellationToken) =>
        {
            if (!TryGetSession(http, sessions))
            {
                return Results.Unauthorized();
            }

            var ids = await ComputeReferencedPractitionerIdsAsync(db, cancellationToken);
            var byId = (await db.Practitioners.AsNoTracking().ToListAsync(cancellationToken))
                .GroupBy(p => p.PractitionerId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var result = ids.Select(id =>
            {
                byId.TryGetValue(id, out var match);
                return new ReferencedPractitionerDto(
                    id,
                    match is null ? null : BuildFullName(match.GivenName, match.MiddleName, match.FamilyName),
                    match is not null);
            }).ToList();

            return Results.Ok(result);
        });

        // Runs the admin-configured BackendSystem practitioner-import workflow (FHIRBridge /run by id, base URL reused
        // from StandaloneBaseUrl), scoped to the submitted practitioner ids via patientSearchCriteria (_id=<ids>) —
        // the same pass-through Provider Standalone's "Fetch Patient List" uses for its criteria box (see
        // FhirSourceConnectorBase.ApplyPatientScopeAsync, which leaves a non-patient-compartment resource type like
        // Practitioner's caller criteria untouched). Each returned Practitioner resource is upserted into the Default
        // Practitioner table, so the view re-reads it through /api/backend-system/practitioners above. Falls back to
        // every referenced id when the request body carries none.
        app.MapPost("/api/backend-system/practitioners/import", async (ImportPractitionersRequest? request, HttpContext http, SessionStore sessions, HealthAppDbContext db, IHttpClientFactory httpClientFactory, ProviderStandaloneCallerIdStore providerStandaloneCallerIds, ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
        {
            if (!TryGetSession(http, sessions))
            {
                return Results.Unauthorized();
            }

            var logger = loggerFactory.CreateLogger("BackendSystemEndpoints");

            var ids = (request?.PractitionerIds ?? [])
                .Select(StripReferencePrefix)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (ids.Count == 0)
            {
                ids = await ComputeReferencedPractitionerIdsAsync(db, cancellationToken);
            }

            if (ids.Count == 0)
            {
                return Results.Ok(new { status = "Succeeded", imported = 0, message = "No practitioner ids to import." });
            }

            var settings = await db.WorkflowSettings.FindAsync(1);
            var workflowId = settings?.BackendSystemPractitionerImportWorkflowId;
            var baseUrl = settings?.StandaloneBaseUrl;
            if (string.IsNullOrWhiteSpace(workflowId) || string.IsNullOrWhiteSpace(baseUrl))
            {
                return Results.Ok(new { status = "Failed", errorMessage = "The Backend System practitioner-import workflow id or the (Provider Standalone) FHIRBridge base URL is not configured. Ask an admin to set both on the Admin → Workflow Settings screen." });
            }

            // FHIR OR-search on the practitioner ids (_id=a,b,c). Practitioner is outside the patient compartment, so
            // FHIRBridge passes this through to the source verbatim rather than trying to patient-scope it.
            var criteria = "_id=" + string.Join(",", ids);
            var runUrl = $"{baseUrl.TrimEnd('/')}/api/v1/workflows/{workflowId}/run";
            // callerId: this workflow runs against the Provider Standalone SourceConnection (see this method's own
            // "(Provider Standalone) FHIRBridge base URL" wording above) — by explicit product decision, Backend
            // System has no interactive sign-in of its own and instead reuses whichever Provider Standalone token
            // was authorized most recently. See ProviderStandaloneCallerIdStore's remarks for why this is safe only
            // as a deliberate, demo-only choice for this app, not a pattern for a real FHIRBridge caller.
            var requestBody = JsonSerializer.Serialize(new
            {
                patientId = (string?)null,
                patientSearchCriteria = criteria,
                callerId = providerStandaloneCallerIds.Get(),
            });

            var client = httpClientFactory.CreateClient("Workflow");
            string? attemptCorrelationId = null;

            // Pre-flight: validate the parameters before the run touches a token or calls Epic. Also what puts
            // this attempt into FHIRBridge's Execution History even when it is refused — a refusal previously
            // produced no run, no outbound call and no exception, so there was nothing to look at afterwards.
            // Deliberately non-fatal on its own failure (an unreachable or older FHIRBridge that predates this
            // endpoint): a pre-flight must never be the reason an import that would have worked doesn't run.
            try
            {
                var validateUrl = $"{baseUrl.TrimEnd('/')}/api/v1/workflows/{workflowId}/validate-run";
                var validateResponse = await client.PostAsync(
                    validateUrl, new StringContent(requestBody, Encoding.UTF8, "application/json"), cancellationToken);

                if (validateResponse.IsSuccessStatusCode)
                {
                    var validation = JsonSerializer.Deserialize<ValidateRunResult>(
                        await validateResponse.Content.ReadAsStringAsync(cancellationToken), WorkflowJsonOptions);

                    if (validation is { IsValid: false })
                    {
                        var reasons = validation.Errors is { Count: > 0 }
                            ? string.Join(" ", validation.Errors.Select(error => error.Message))
                            : "This workflow cannot be run with the values supplied.";
                        return Results.Ok(new { status = "Failed", errorMessage = reasons, correlationId = validation.CorrelationId });
                    }

                    // Echo the attempt-scoped id validate-run minted onto the run below, so both land on one
                    // Execution History row and every outbound EHR call the run makes is filed under it too.
                    attemptCorrelationId = validation?.CorrelationId;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "validate-run pre-flight could not be completed for workflow {WorkflowId}; continuing to the run.", workflowId);
            }

            HttpResponseMessage response;
            try
            {
                using var runRequest = new HttpRequestMessage(HttpMethod.Post, runUrl)
                {
                    Content = new StringContent(requestBody, Encoding.UTF8, "application/json"),
                };
                if (!string.IsNullOrWhiteSpace(attemptCorrelationId))
                {
                    runRequest.Headers.TryAddWithoutValidation("X-Correlation-Id", attemptCorrelationId);
                }

                response = await client.SendAsync(runRequest, cancellationToken);
            }
            catch (Exception ex)
            {
                return Results.Ok(new { status = "Failed", errorMessage = $"Could not reach FHIRBridge to run the import workflow: {ex.Message}" });
            }

            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                // A token/node failure throws past FHIRBridge's orchestrator before the run returns, surfacing as a
                // non-2xx with a bare {"error":"..."} body — surface that reason rather than just the status code.
                return Results.Ok(new { status = "Failed", errorMessage = TryReadErrorMessage(responseBody) ?? $"Import workflow run failed with status {(int)response.StatusCode}." });
            }

            WorkflowRunResult? runResult;
            try
            {
                runResult = JsonSerializer.Deserialize<WorkflowRunResult>(responseBody, WorkflowJsonOptions);
            }
            catch (JsonException ex)
            {
                return Results.Ok(new { status = "Failed", errorMessage = $"Unexpected workflow response: {ex.Message}" });
            }

            var practitionerResources = (runResult?.OutputsByNodeId?.Values ?? Enumerable.Empty<WorkflowNodeOutput>())
                .SelectMany(output => output?.Payload?.Resources ?? [])
                .Where(resource => string.Equals(resource.ResourceType, "Practitioner", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (practitionerResources.Count == 0)
            {
                return Results.Ok(new { status = "Failed", errorMessage = "The workflow run returned no Practitioner resources." });
            }

            var imported = 0;
            foreach (var resource in practitionerResources)
            {
                if (string.IsNullOrWhiteSpace(resource.Payload))
                {
                    continue;
                }

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

                var existing = await db.Practitioners.FirstOrDefaultAsync(p => p.PractitionerId == id, cancellationToken);
                if (existing is null)
                {
                    existing = new PractitionerEntity { PractitionerId = id };
                    db.Practitioners.Add(existing);
                }

                // PractitionerFieldExtractor targets the _11 shape; map its fields onto this table's columns
                // (Given/Family/Qualification). MiddleName/Identifier are left as-is — the extractor supplies neither.
                existing.GivenName = fields.FirstName;
                existing.FamilyName = fields.LastName;
                existing.NPI = fields.NPI;
                existing.Gender = fields.Gender;
                existing.Qualification = fields.Credential ?? fields.Specialty;
                existing.Phone = fields.Phone;
                existing.Email = fields.Email;
                imported++;
            }

            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(new { status = "Succeeded", imported });
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

        // Wipes every table the Default (non-"New 11") BackendSystem screens read from — the "Clear Data" button on
        // the Patient List / Practitioners views. Deliberately leaves the _11 curated tables (Resource11Endpoints.cs)
        // and every other role's data untouched; this only resets the tables BackendSystemEndpoints.cs itself reads/
        // writes above. ExecuteDeleteAsync issues a single bulk DELETE per table rather than loading rows into the
        // change tracker first.
        app.MapPost("/api/backend-system/clear-data", async (HttpContext http, SessionStore sessions, HealthAppDbContext db, CancellationToken cancellationToken) =>
        {
            if (!TryGetSession(http, sessions))
            {
                return Results.Unauthorized();
            }

            await db.MedicationAdministrations.ExecuteDeleteAsync(cancellationToken);
            await db.MedicationRequests.ExecuteDeleteAsync(cancellationToken);
            await db.DiagnosticReports.ExecuteDeleteAsync(cancellationToken);
            await db.ServiceRequests.ExecuteDeleteAsync(cancellationToken);
            await db.Procedures.ExecuteDeleteAsync(cancellationToken);
            await db.Conditions.ExecuteDeleteAsync(cancellationToken);
            await db.Observations.ExecuteDeleteAsync(cancellationToken);
            await db.AllergyIntolerances.ExecuteDeleteAsync(cancellationToken);
            await db.Encounters.ExecuteDeleteAsync(cancellationToken);
            await db.Practitioners.ExecuteDeleteAsync(cancellationToken);
            await db.BackendSystemPatients.ExecuteDeleteAsync(cancellationToken);

            return Results.Ok(new { status = "Succeeded" });
        });
    }

    // Distinct practitioner ids referenced across the Default clinical tables that carry a PractitionerId, normalized
    // to bare ids (a reference column may hold "Practitioner/{id}"). "All unique referenced", not "missing only" —
    // an already-imported id still appears (the view flags it), so a re-import can refresh existing rows too.
    private static async Task<List<string>> ComputeReferencedPractitionerIdsAsync(HealthAppDbContext db, CancellationToken cancellationToken)
    {
        var references = new List<string?>();
        references.AddRange(await db.Encounters.Where(e => e.PractitionerId != null).Select(e => e.PractitionerId).ToListAsync(cancellationToken));
        references.AddRange(await db.Procedures.Where(p => p.PractitionerId != null).Select(p => p.PractitionerId).ToListAsync(cancellationToken));
        references.AddRange(await db.ServiceRequests.Where(s => s.PractitionerId != null).Select(s => s.PractitionerId).ToListAsync(cancellationToken));
        references.AddRange(await db.MedicationRequests.Where(m => m.PractitionerId != null).Select(m => m.PractitionerId).ToListAsync(cancellationToken));

        return references
            .Select(StripReferencePrefix)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // FHIRBridge surfaces a failed run as a non-2xx with a bare {"error":"..."} body — pull that message out when
    // present, otherwise let the caller fall back to a status-code message.
    private static string? TryReadErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String)
            {
                return error.GetString();
            }
        }
        catch (JsonException)
        {
            // Non-JSON error body — nothing to extract.
        }

        return null;
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

// One row of GET /api/backend-system/practitioners/referenced-ids — a practitioner id referenced by another Default
// table, with its Practitioner-table name (if already present) and whether it's been imported yet.
record ReferencedPractitionerDto(string PractitionerId, string? Name, bool Imported);

// Body of POST /api/backend-system/practitioners/import — the practitioner ids to fetch + upsert (nullable/empty
// means "use every referenced id").
record ImportPractitionersRequest(List<string>? PractitionerIds);

// The subset of FHIRBridge's RankedWorkflowOrchestrator /run response this app reads to pull Practitioner resources
// out of a successful run (mirrors launch-standalone-provider.ts's WorkflowRunResponse shape).
record WorkflowRunResult(WorkflowRunInfo? WorkflowRun, Dictionary<string, WorkflowNodeOutput>? OutputsByNodeId);

/// <summary>Matches FHIRBridge's POST /api/v1/workflows/{id}/validate-run response.</summary>
record ValidateRunResult(bool IsValid, string? CorrelationId, string? WorkflowRunId, List<ValidateRunError>? Errors);

record ValidateRunError(string? Parameter, string? Message);
record WorkflowRunInfo(string? Status, string? ErrorMessage);
record WorkflowNodeOutput(string? NodeType, WorkflowNodePayload? Payload);
record WorkflowNodePayload(List<WorkflowRunResource>? Resources);
record WorkflowRunResource(string? ResourceType, string? ResourceId, string? Payload);

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
