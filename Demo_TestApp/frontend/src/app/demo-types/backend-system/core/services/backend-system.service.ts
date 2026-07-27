import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, map } from 'rxjs';
import { environment } from '../../../../../environments/environment';
import { PatientDetail, PatientListItem } from '../models/backend-system.model';
import { ResourceMenuItem } from '../config/backend-system-menu.config';

const BACKEND_BASE_URL = environment.healthAppBase;

@Injectable({ providedIn: 'root' })
export class BackendSystemService {
  private readonly http = inject(HttpClient);

  getPatients(): Observable<PatientListItem[]> {
    return this.http.get<PatientListItem[]>(`${BACKEND_BASE_URL}/api/backend-system/patients`, {
      withCredentials: true,
    });
  }

  getPatient(patientId: string): Observable<PatientDetail> {
    return this.http.get<PatientDetail>(
      `${BACKEND_BASE_URL}/api/backend-system/patient/${encodeURIComponent(patientId)}`,
      { withCredentials: true },
    );
  }

  /**
   * Loads the rows for one left-nav menu item. Every menu item other than "Patient" backs onto a list endpoint
   * that already returns an array; "Patient" itself (apiSegment === '') backs onto the single-object detail
   * endpoint instead, so its result is wrapped in a one-element array here — that lets PatientDetailsComponent's
   * table render every menu item, including Patient, through one shared code path instead of a special case.
   */
  getResourceRows(patientId: string, menuItem: ResourceMenuItem): Observable<Record<string, unknown>[]> {
    if (!menuItem.apiSegment) {
      return this.getPatient(patientId).pipe(
        map((patient) => [patient as unknown as Record<string, unknown>]),
      );
    }

    return this.http.get<Record<string, unknown>[]>(
      `${BACKEND_BASE_URL}/api/backend-system/patient/${encodeURIComponent(patientId)}/${menuItem.apiSegment}`,
      { withCredentials: true },
    );
  }
}
