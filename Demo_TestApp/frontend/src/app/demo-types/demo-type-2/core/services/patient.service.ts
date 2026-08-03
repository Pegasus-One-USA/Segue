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
  getPatient(workflowRunId: string | null): Observable<Patient> {
    if (!workflowRunId) {
      return of(MOCK_PATIENT).pipe(delay(600));
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
            return this.toPatient(response.patient);
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
