import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { HAPI_TERMINOLOGY_ENDPOINTS } from '../../core/api-endpoints';
import { TerminologyImportHistoryEntry } from '../../settings/components/terminology-import-history/terminology-import-history.component';

export interface HapiCredentialField {
  name: string;
  label: string;
  hasValue: boolean;
}

export interface HapiTerminologyConfiguration {
  code: string;
  displayName: string;
  schedulerEnabled: boolean;
  frequency: string;
  frequencyOptions: string[];
  executionTime: string;
  lastRunUtc: string | null;
  credentials: HapiCredentialField[];
  /** Non-null only for systems whose HAPI sync reads a configurable download endpoint (currently LOINC
   * only) — unlike credentials this is a plain visible value, not write-only. */
  downloadApiUrl: string | null;
}

export interface UpdateHapiTerminologyConfiguration {
  schedulerEnabled: boolean;
  frequency: string;
  executionTime: string;
  credentialValues?: Record<string, string> | null;
  downloadApiUrl?: string | null;
}

export interface HapiTerminologyRunStartedResponse {
  message: string;
}

/** Grouped settings, manual Run Now, and history for the 13 HAPI-terminology-server sync systems
 * (Settings → System Settings → General → Terminology Servers). */
@Injectable({ providedIn: 'root' })
export class HapiTerminologyConfigurationService {
  private readonly http = inject(HttpClient);

  getAll(): Observable<HapiTerminologyConfiguration[]> {
    return this.http.get<HapiTerminologyConfiguration[]>(HAPI_TERMINOLOGY_ENDPOINTS.list);
  }

  get(code: string): Observable<HapiTerminologyConfiguration> {
    return this.http.get<HapiTerminologyConfiguration>(HAPI_TERMINOLOGY_ENDPOINTS.configuration(code));
  }

  update(code: string, value: UpdateHapiTerminologyConfiguration): Observable<HapiTerminologyConfiguration> {
    return this.http.put<HapiTerminologyConfiguration>(HAPI_TERMINOLOGY_ENDPOINTS.configuration(code), value);
  }

  // Runs in the background on the server — this only confirms the sync job was queued.
  runNow(code: string): Observable<HapiTerminologyRunStartedResponse> {
    return this.http.post<HapiTerminologyRunStartedResponse>(HAPI_TERMINOLOGY_ENDPOINTS.runNow(code), {});
  }

  getHistory(code: string): Observable<TerminologyImportHistoryEntry[]> {
    return this.http.get<TerminologyImportHistoryEntry[]>(HAPI_TERMINOLOGY_ENDPOINTS.history(code));
  }
}
