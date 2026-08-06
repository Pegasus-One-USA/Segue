import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../../../environments/environment';
import { Resource11MenuItem } from '../config/resource-11-menu.config';

const BACKEND_BASE_URL = environment.healthAppBase;

/** A practitioner referenced by other _11 tables but not yet present in Practitioner_11 (see the Practitioner tab). */
export interface MissingPractitioner {
  practitionerId: string;
  name: string | null;
}

/** Result of POST /api/v11/practitioners/import. */
export interface ImportResult {
  status: 'Succeeded' | 'Failed';
  imported?: number;
  message?: string;
  errorMessage?: string;
}

// Loads the rows for one "New 11" resource from its /api/v11/{apiSegment} endpoint. Rows come back as generic
// records (dynamic columns driven by resource-11-menu.config.ts), so the browser can render every resource type
// through one shared code path rather than a per-resource component.
@Injectable({ providedIn: 'root' })
export class Resource11Service {
  private readonly http = inject(HttpClient);

  getRows(menuItem: Resource11MenuItem): Observable<Record<string, unknown>[]> {
    return this.http.get<Record<string, unknown>[]>(
      `${BACKEND_BASE_URL}/api/v11/${menuItem.apiSegment}`,
      { withCredentials: true },
    );
  }

  // One clinical resource's rows for a single patient — backs the per-patient tabs (Patient List -> patient).
  getPatientResourceRows(patientId: string, menuItem: Resource11MenuItem): Observable<Record<string, unknown>[]> {
    return this.http.get<Record<string, unknown>[]>(
      `${BACKEND_BASE_URL}/api/v11/patient/${encodeURIComponent(patientId)}/${menuItem.apiSegment}`,
      { withCredentials: true },
    );
  }

  // Practitioners referenced across the other _11 tables that have no Practitioner_11 row yet.
  getMissingPractitioners(): Observable<MissingPractitioner[]> {
    return this.http.get<MissingPractitioner[]>(
      `${BACKEND_BASE_URL}/api/v11/practitioners/missing`,
      { withCredentials: true },
    );
  }

  // Runs the logged-in role's configured New 11 workflow to fetch + upsert the missing practitioners. callerId
  // (when the calling role has one — see ProviderStandaloneNew11Component) is forwarded to FHIRBridge's /run so a
  // Provider/Patient Standalone source's CallerId-keyed interactive token cache (see
  // SmartAuthorizationCodeTokenProvider.BuildStoreKey) finds the same token this session's OAuth sign-in already
  // established, instead of the request carrying none and finding nothing cached. practitionerIds (when sent —
  // see PROVIDER_STANDALONE_PRACTITIONER_IDS) is an explicit, curated id list that wins server-side over the
  // "missing ids" auto-discovery every other role still relies on by omitting it.
  importPractitioners(callerId?: string, practitionerIds?: string[]): Observable<ImportResult> {
    const body: { callerId?: string; practitionerIds?: string[] } = {};
    if (callerId) {
      body.callerId = callerId;
    }
    if (practitionerIds?.length) {
      body.practitionerIds = practitionerIds;
    }
    return this.http.post<ImportResult>(
      `${BACKEND_BASE_URL}/api/v11/practitioners/import`,
      body,
      { withCredentials: true },
    );
  }
}
