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

  // Runs the logged-in role's configured New 11 workflow to fetch + upsert the missing practitioners.
  importPractitioners(): Observable<ImportResult> {
    return this.http.post<ImportResult>(
      `${BACKEND_BASE_URL}/api/v11/practitioners/import`,
      {},
      { withCredentials: true },
    );
  }
}
