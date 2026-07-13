import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { Observable, catchError, delay, map, of } from 'rxjs';
import { Patient } from '../models/patient.model';
import { MOCK_PATIENT } from '../mock-data/patient.mock';
import { FHIRBRIDGE_BASE_URL } from '../config/launch.config';

interface LaunchResultResponse {
  patient: FhirPatient | null;
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
  private readonly route = inject(ActivatedRoute);

  /**
   * Returns the patient context for the current launch. When the page was reached via FHIRBridge's post-launch
   * redirect (?workflowRunId=...), fetches the raw Patient resource FHIRBridge's Epic source node retrieved for
   * that run (the same data the "Execution History" screen shows) and binds directly from its FHIR shape. Falls
   * back to mock data when there's no launch context at all, or if the fetch fails.
   */
  getPatient(): Observable<Patient> {
    const workflowRunId = this.route.snapshot.queryParamMap.get('workflowRunId');
    if (!workflowRunId) {
      return of(MOCK_PATIENT).pipe(delay(600));
    }

    return this.http
      .get<LaunchResultResponse>(`${FHIRBRIDGE_BASE_URL}/api/v1/workflows/runs/${workflowRunId}/launch-result`)
      .pipe(
        map((response) => (response.patient ? this.toPatient(response.patient) : MOCK_PATIENT)),
        catchError(() => of(MOCK_PATIENT)),
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
