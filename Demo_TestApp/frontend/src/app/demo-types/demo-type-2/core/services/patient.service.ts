import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, delay, firstValueFrom, from, map, mergeMap, of, throwError } from 'rxjs';
import { Patient } from '../models/patient.model';
import { MOCK_PATIENT } from '../mock-data/patient.mock';
import { FHIRBRIDGE_BASE_URL } from '../config/launch.config';
import { environment } from '../../../../../environments/environment';

// HealthApp's own backend (Demo_TestApp), not FHIRBridge.
const HEALTHAPP_BACKEND_BASE_URL = environment.healthAppBase;

// Same "not configured" shape as launch-provider-in-app.ts's isConfiguredBaseUrl — guards against both an empty
// value and the literal placeholder left in appsettings.json's DefaultWorkflowSettings:StandaloneBaseUrl
// (REPLACE_WITH_PUBLIC_URL) for any environment that hasn't set a real one yet.
function isConfiguredBaseUrl(value: string | null | undefined): boolean {
  return !!value && value.trim().length > 0 && value !== 'http://REPLACE_WITH_PUBLIC_URL';
}

interface LaunchResultResponse {
  patient: FhirPatient | null;
  // Per-resource-type counts of everything the run's source node fetched (Patient, Condition, Observation, …).
  resourceCounts?: Record<string, number>;
}

/** What getPatient resolves to: the bound Patient plus the run's per-resource-type fetch counts. */
export interface LaunchOutcome {
  patient: Patient;
  resourceCounts: Record<string, number>;
}

// standaloneBaseUrl is WorkflowSettingsEntity.StandaloneBaseUrl — deliberately reused from Provider_Standalone
// rather than a dedicated field (see launch-provider-in-app.ts's ProviderInAppLaunchContext).
interface ProviderInAppLaunchContext {
  standaloneBaseUrl: string;
}

/** The subset of a raw FHIR R4 Patient resource this demo binds to. Everything is optional — Epic's own data
 *  decides what's actually present, and any field it didn't send is left blank in the UI rather than guessed. */
interface FhirPatient {
  name?: Array<{ family?: string; given?: string[]; text?: string }>;
  gender?: string;
  birthDate?: string;
  active?: boolean;
  deceasedBoolean?: boolean;
  deceasedDateTime?: string;
  maritalStatus?: { text?: string; coding?: Array<{ display?: string }> };
  telecom?: Array<{ system?: string; value?: string }>;
  address?: Array<{
    line?: string[];
    city?: string;
    state?: string;
    postalCode?: string;
    country?: string;
    period?: { start?: string };
  }>;
  communication?: Array<{ language?: { text?: string; coding?: Array<{ display?: string }> } }>;
  managingOrganization?: { display?: string };
  extension?: FhirExtension[];
}

/** A FHIR extension can carry its value directly (valueString/valueCode/valueCodeableConcept) or as nested
 *  sub-extensions (US Core race/ethnicity's "text"/"ombCategory" pattern) — both shapes show up in real Epic data. */
interface FhirExtension {
  url?: string;
  valueString?: string;
  valueCode?: string;
  valueCodeableConcept?: { text?: string; coding?: Array<{ display?: string }> };
  extension?: FhirExtension[];
}

/** One row of Demo_TestApp's GET /api/pipeline-runs/{runId}/patients — a Patient_NewMapped row, i.e. what a
 *  FHIRBridge run actually LANDED in HealthAppDb, as opposed to the raw FHIR resource it fetched from the EHR.
 *  Every field is nullable because the destination's mapping profile decides which columns it targets; anything
 *  it didn't map stays NULL in the table and renders blank in the UI rather than being guessed at. */
interface MappedPatientRow {
  patientId: string;
  identifier: string | null;
  mrn: string | null;
  familyName: string | null;
  givenName: string | null;
  middleName: string | null;
  fullName: string | null;
  gender: string | null;
  birthDate: string | null;
  deceased: boolean | null;
  maritalStatus: string | null;
  phone: string | null;
  email: string | null;
  addressLine1: string | null;
  addressLine2: string | null;
  city: string | null;
  state: string | null;
  postalCode: string | null;
  country: string | null;
  pipelineRunId: string | null;
}

interface PipelineRunPatientsResponse {
  pipelineRunId: string;
  count: number;
  patients: MappedPatientRow[];
}

/** The mapping engine has no array-collapsing step, so a column its profile targeted from a repeating FHIR
 *  element can land as a raw JSON array literal — observed in the live table as a WHOLE value
 *  (GivenName = '["deana","b"]', AddressLine1 = '["3523 Maple Street"]') and EMBEDDED inside one
 *  (FullName = '["deana","b"] ALLERGY', where the mapping composed a name from an already-array given name).
 *  Both are unwrapped here, at the read boundary, so the UI never shows brackets and quotes to a clinician.
 *  A plain scalar passes through untouched, and a bracketed run that isn't valid JSON is left exactly as
 *  stored rather than mangled — better to show the raw value than to guess at it. */
function unwrapMappedValue(value: string | null): string | null {
  if (value === null) {
    return null;
  }

  // Rewrite every [...] run in place, which covers the embedded case; a value that is ENTIRELY one array
  // literal is just the special case where the single match spans the whole string.
  const unwrapped = value.replace(/\[[^\[\]]*\]/g, (literal) => {
    try {
      const parsed: unknown = JSON.parse(literal);
      if (!Array.isArray(parsed)) {
        return literal;
      }
      return parsed
        .filter((entry) => entry !== null && entry !== undefined && entry !== '')
        .map((entry) => String(entry).trim())
        .filter((entry) => entry.length > 0)
        .join(' ');
    } catch {
      return literal;
    }
  });

  // Collapse whitespace left behind by an array that unwrapped to nothing (e.g. '[] ALLERGY' -> 'ALLERGY').
  const collapsed = unwrapped.replace(/\s+/g, ' ').trim();
  return collapsed.length > 0 ? collapsed : null;
}

/** Blank-not-null normalizer: the table's NULLs and the UI's "no value" are the same thing here, and the
 *  template renders a null/empty field as truly empty (see LaunchProviderInAppComponent.displayValue). */
function textOrEmpty(value: string | null): string {
  return unwrapMappedValue(value) ?? '';
}

@Injectable({ providedIn: 'root' })
export class PatientService {
  private readonly http = inject(HttpClient);

  /**
   * Returns the patient context for the current launch. When the page was reached via FHIRBridge's post-launch
   * redirect (?workflowRunId=...), fetches the raw Patient resource FHIRBridge's Epic source node retrieved for
   * that run (the same data the "Execution History" screen shows) and binds directly from its FHIR shape. Falls
   * back to mock data only when there's no launch context at all (the page was opened directly, not via a real
   * EHR launch) — a real launch that fails to produce patient data errors out instead of masking the failure
   * behind mock data that looks like a working demo (see launch-provider-in-app.ts's launchError handling).
   *
   * Takes workflowRunId as a caller-supplied value rather than reading window.location.search itself: the caller
   * (LaunchProviderInAppComponent.ngOnInit) strips this query param from the URL right after reading it — a stale
   * ?workflowRunId= left sitting in the address bar would otherwise get silently re-fetched and shown to whichever
   * HealthApp account is logged in next on the same tab (that run's own result endpoint has no per-caller
   * ownership check), not just the account whose real EHR launch actually produced it.
   */
  getPatient(workflowRunId: string | null): Observable<LaunchOutcome> {
    if (!workflowRunId) {
      return of({ patient: MOCK_PATIENT, resourceCounts: {} }).pipe(delay(600));
    }

    return this.resolveBaseUrl().pipe(
      mergeMap((baseUrl) =>
        this.http.get<LaunchResultResponse>(`${baseUrl}/api/v1/workflows/runs/${workflowRunId}/launch-result`).pipe(
          map((response) => {
            if (!response.patient) {
              throw new Error(
                `FHIRBridge's run ${workflowRunId} completed but returned no Patient resource — check Execution History for this run.`,
              );
            }
            return { patient: this.toPatient(response.patient), resourceCounts: response.resourceCounts ?? {} };
          }),
          catchError((error: unknown) => {
            const detail =
              error instanceof HttpErrorResponse
                ? `${error.status || 'network error'} calling ${baseUrl}`
                : error instanceof Error
                  ? error.message
                  : 'unknown error';
            return throwError(() => new Error(`Failed to load this launch's patient data (${detail}).`));
          }),
        ),
      ),
    );
  }

  /**
   * The bound patient AS LANDED by the run, read from HealthAppDb's own Patient_NewMapped table via
   * Demo_TestApp's GET /api/pipeline-runs/{runId}/patients — not from FHIRBridge's launch-result (which returns
   * the raw FHIR resource fetched from the EHR, pre-mapping). Same run id either way: the PipelineRunId stamped
   * on each written row IS the WorkflowRunId that arrives as ?workflowRunId= on the post-launch redirect (see
   * MappedDestinationRecord construction in the Runtime node executors), so the launch param needs no translation.
   *
   * Resolves to null when the run wrote no Patient row — a real outcome (a workflow can target other resource
   * types), which the caller surfaces as "nothing landed for this run" rather than as a failure.
   */
  getMappedPatient(workflowRunId: string): Observable<Patient | null> {
    return this.http
      .get<PipelineRunPatientsResponse>(
        `${HEALTHAPP_BACKEND_BASE_URL}/api/pipeline-runs/${encodeURIComponent(workflowRunId)}/patients`,
        { withCredentials: true },
      )
      .pipe(
        // One row per patient per run; this screen shows a single patient context, so the first row is it.
        map((response) => (response.patients.length > 0 ? this.toPatientFromMappedRow(response.patients[0]) : null)),
        catchError((error: unknown) => {
          const detail =
            error instanceof HttpErrorResponse
              ? `${error.status || 'network error'} calling HealthApp backend`
              : error instanceof Error
                ? error.message
                : 'unknown error';
          return throwError(() => new Error(`Failed to load this run's mapped patient data (${detail}).`));
        }),
      );
  }

  /**
   * Binds a Patient_NewMapped row onto the Patient shape the template already renders. Only the columns that
   * table actually has are populated; fields with no corresponding column (legalSex, sexForClinicalUse,
   * pronouns, race, ethnicity, language, managingOrganization) are left blank rather than carried over from a
   * previous FHIR-sourced binding — the table is the single source of truth for this screen, so showing a value
   * it doesn't hold would be inventing data.
   */
  private toPatientFromMappedRow(row: MappedPatientRow): Patient {
    const given = textOrEmpty(row.givenName);
    const middle = textOrEmpty(row.middleName);
    const family = textOrEmpty(row.familyName);
    // Prefer the mapped FullName column, but fall back to composing the parts when it's NULL/unmapped.
    const composed = [given, middle, family].filter((part) => part.length > 0).join(' ');

    // AddressLine1 and AddressLine2 are separate columns that the live data shows can hold the SAME value
    // (both '["3523 Maple Street"]' for the seeded row) — de-duplicate so the street doesn't render twice.
    const line1 = textOrEmpty(row.addressLine1);
    const line2 = textOrEmpty(row.addressLine2);
    const street = [line1, line2 === line1 ? '' : line2].filter((part) => part.length > 0).join(', ');

    return {
      fullName: textOrEmpty(row.fullName) || composed,
      firstName: given,
      lastName: family,
      gender: textOrEmpty(row.gender),
      // No Patient_NewMapped columns for these — blank, not inferred from gender (legal sex and clinical sex
      // are deliberately distinct concepts from administrative gender).
      legalSex: '',
      sexForClinicalUse: '',
      pronouns: '',
      dateOfBirth: textOrEmpty(row.birthDate),
      maritalStatus: textOrEmpty(row.maritalStatus),
      // The table has no Active column, so "Status" can't be sourced from it. A landed row says nothing about
      // administrative active-ness; true keeps the existing display behavior for the common case.
      active: true,
      deceased: row.deceased ?? false,
      contact: {
        email: unwrapMappedValue(row.email),
        phone: unwrapMappedValue(row.phone),
      },
      address: {
        street: street.length > 0 ? street : null,
        city: unwrapMappedValue(row.city),
        state: unwrapMappedValue(row.state),
        stateAbbreviation: null,
        zip: unwrapMappedValue(row.postalCode),
        country: unwrapMappedValue(row.country),
        // No period/valid-since column on the table.
        validSince: null,
      },
      languagePreference: {
        language: null,
        mode: null,
      },
      demographics: {
        race: null,
        ethnicity: null,
      },
      managingOrganization: '',
    };
  }

  // Admin-editable via the unified Admin Settings screen (see AdminSettingsComponent) — persisted on
  // Demo_TestApp's own backend (WorkflowSettingsEntity.StandaloneBaseUrl, reused from Provider_Standalone rather
  // than a dedicated field), read at runtime rather than baked in at build time. Falls back to the build-time
  // FHIRBRIDGE_BASE_URL constant (only ever correct when the browser and FHIRBridge Api share a host) if this
  // lookup fails or hasn't been configured, same as launch-provider-in-app.ts's own baseUrl field (which this
  // deliberately duplicates rather than shares, since that component and this root-provided singleton have no
  // shared instance to read it off of).
  private resolveBaseUrl(): Observable<string> {
    return from(
      firstValueFrom(
        this.http.get<ProviderInAppLaunchContext>(`${HEALTHAPP_BACKEND_BASE_URL}/api/provider-in-app-launch-context`, {
          withCredentials: true,
        }),
      )
        .then((current) => (isConfiguredBaseUrl(current.standaloneBaseUrl) ? current.standaloneBaseUrl : FHIRBRIDGE_BASE_URL))
        .catch(() => FHIRBRIDGE_BASE_URL),
    );
  }

  private toPatient(fhir: FhirPatient): Patient {
    const name = fhir.name?.[0];
    const given = name?.given?.join(' ') ?? '';
    const family = name?.family ?? '';
    const address = fhir.address?.[0];
    const language = fhir.communication?.[0]?.language;

    return {
      fullName: name?.text || [given, family].filter((part) => !!part).join(' '),
      firstName: given,
      lastName: family,
      gender: fhir.gender ?? '',
      legalSex: this.extensionDisplay(fhir, 'legal-sex') ?? '',
      sexForClinicalUse: this.extensionDisplay(fhir, 'sex-for-clinical-use') ?? '',
      pronouns: this.extensionDisplay(fhir, 'calculated-pronouns-to-use-for-text') ?? '',
      dateOfBirth: fhir.birthDate ?? '',
      maritalStatus: fhir.maritalStatus?.text ?? fhir.maritalStatus?.coding?.[0]?.display ?? '',
      active: fhir.active ?? true,
      deceased: !!(fhir.deceasedBoolean || fhir.deceasedDateTime),
      contact: {
        email: this.telecomValue(fhir, 'email'),
        phone: this.telecomValue(fhir, 'phone'),
      },
      address: {
        street: address?.line?.join(', ') ?? null,
        city: address?.city ?? null,
        state: address?.state ?? null,
        stateAbbreviation: null,
        zip: address?.postalCode ?? null,
        country: address?.country ?? null,
        validSince: address?.period?.start ?? null,
      },
      languagePreference: {
        language: language?.text ?? language?.coding?.[0]?.display ?? null,
        mode: null,
      },
      demographics: {
        race: this.extensionDisplay(fhir, 'us-core-race'),
        ethnicity: this.extensionDisplay(fhir, 'us-core-ethnicity'),
      },
      managingOrganization: fhir.managingOrganization?.display ?? '',
    };
  }

  private telecomValue(fhir: FhirPatient, system: string): string | null {
    return fhir.telecom?.find((entry) => entry.system === system)?.value ?? null;
  }

  /**
   * Best-effort display text for an extension found by URL suffix (e.g. Epic's own "legal-sex" or US Core's
   * "us-core-race"), across the shapes actually seen in practice: a direct valueCodeableConcept.text/coding
   * display, a direct valueString, or a nested "text" sub-extension (US Core race/ethnicity's pattern). Returns
   * null — not "Not Available" — when Epic didn't send the extension at all, which the UI treats identically.
   */
  private extensionDisplay(fhir: FhirPatient, urlSuffix: string): string | null {
    const extension = fhir.extension?.find((entry) => entry.url?.endsWith(urlSuffix));
    if (!extension) {
      return null;
    }

    if (extension.valueCodeableConcept?.text) {
      return extension.valueCodeableConcept.text;
    }
    if (extension.valueCodeableConcept?.coding?.[0]?.display) {
      return extension.valueCodeableConcept.coding[0].display;
    }
    if (extension.valueString) {
      return extension.valueString;
    }

    const textSubExtension = extension.extension?.find((entry) => entry.url === 'text');
    return textSubExtension?.valueString ?? null;
  }
}
