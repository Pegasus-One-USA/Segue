import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, map } from 'rxjs';
import { environment } from '../../../../../environments/environment';
import {
  ClearDataResult,
  PatientDetail,
  PatientListItem,
  Practitioner,
  PractitionerImportResult,
  ReferencedPractitioner,
} from '../models/backend-system.model';
import { ResourceMenuItem } from '../config/backend-system-menu.config';

const BACKEND_BASE_URL = environment.healthAppBase;

/** The three stores the BackendSystem Patient List / Patient Details screens can read from — see the
 *  "Data Source" radio group in backend-system.html and PatientDataSourceReaders.cs on the backend. Every other
 *  resource tab on the Patient Details screen (Encounters, Observations, ...) is unaffected and always reads
 *  SQL Server, so this type only ever needs to travel to the two endpoints below. */
export type PatientDataSource = 'sql' | 'mysql' | 'nosql';

@Injectable({ providedIn: 'root' })
export class BackendSystemService {
  private readonly http = inject(HttpClient);

  getPatients(dataSource: PatientDataSource): Observable<PatientListItem[]> {
    return this.http.get<PatientListItem[]>(`${BACKEND_BASE_URL}/api/backend-system/patients`, {
      withCredentials: true,
      params: new HttpParams().set('source', dataSource),
    });
  }

  getPatient(patientId: string, dataSource: PatientDataSource): Observable<PatientDetail> {
    return this.http.get<PatientDetail>(
      `${BACKEND_BASE_URL}/api/backend-system/patient/${encodeURIComponent(patientId)}`,
      { withCredentials: true, params: new HttpParams().set('source', dataSource) },
    );
  }

  /**
   * Loads the rows for one left-nav menu item. Every menu item other than "Patient" backs onto a list endpoint
   * that already returns an array; "Patient" itself (apiSegment === '') backs onto the single-object detail
   * endpoint instead, so its result is wrapped in a one-element array here — that lets PatientDetailsComponent's
   * table render every menu item, including Patient, through one shared code path instead of a special case.
   * dataSource only applies to that "Patient" branch — every other menu item's own sub-resource route always
   * reads SQL Server, unaffected by the radio group.
   */
  getResourceRows(
    patientId: string,
    menuItem: ResourceMenuItem,
    dataSource: PatientDataSource,
  ): Observable<Record<string, unknown>[]> {
    if (!menuItem.apiSegment) {
      return this.getPatient(patientId, dataSource).pipe(
        map((patient) => [patient as unknown as Record<string, unknown>]),
      );
    }

    return this.http.get<Record<string, unknown>[]>(
      `${BACKEND_BASE_URL}/api/backend-system/patient/${encodeURIComponent(patientId)}/${menuItem.apiSegment}`,
      { withCredentials: true },
    );
  }

  /** Every practitioner already stored in HealthDB's own Practitioner table (the Practitioners view's table). */
  getPractitioners(): Observable<Practitioner[]> {
    return this.http.get<Practitioner[]>(`${BACKEND_BASE_URL}/api/backend-system/practitioners`, {
      withCredentials: true,
    });
  }

  /** Practitioner ids referenced across the Default clinical tables, each flagged with whether it's imported yet —
   *  pre-filled into the "Import Practitioner" input. */
  getReferencedPractitionerIds(): Observable<ReferencedPractitioner[]> {
    return this.http.get<ReferencedPractitioner[]>(
      `${BACKEND_BASE_URL}/api/backend-system/practitioners/referenced-ids`,
      { withCredentials: true },
    );
  }

  /** Runs the configured import workflow for the given practitioner ids and upserts the results into the
   *  Practitioner table. */
  importPractitioners(practitionerIds: string[]): Observable<PractitionerImportResult> {
    return this.http.post<PractitionerImportResult>(
      `${BACKEND_BASE_URL}/api/backend-system/practitioners/import`,
      { practitionerIds },
      { withCredentials: true },
    );
  }

  /** Wipes every table the Default BackendSystem screens read from (Patient_NewMapped, Practitioner, and every
   *  clinical resource table) — the "Clear Data" button on the Patient List / Practitioners views. */
  clearData(): Observable<ClearDataResult> {
    return this.http.post<ClearDataResult>(
      `${BACKEND_BASE_URL}/api/backend-system/clear-data`,
      {},
      { withCredentials: true },
    );
  }
}
