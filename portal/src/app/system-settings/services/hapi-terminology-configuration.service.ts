import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { HAPI_TERMINOLOGY_ENDPOINTS } from '../../core/api-endpoints';
import { TerminologyImportHistoryEntry } from '../../settings/components/terminology-import-history/terminology-import-history.component';

export interface PagedResult<T> {
  items: T[];
  totalCount: number;
  page: number;
  pageSize: number;
}

/** One code/description row from a HAPI terminology system's local store (TRM_CONCEPT) — shown on
 * the "View All Codes" screen under each system's ⋮ menu. */
export interface TerminologyConcept {
  pid: number;
  code: string;
  display: string | null;
}

export interface UpsertTerminologyConcept {
  code: string;
  display: string | null;
}

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

/** Result of checking one system's official source for a newer version than what's stored locally,
 * without downloading/importing anything. `supported` is false for systems whose source has no
 * discoverable "latest version" pointer — in that case `latestAvailableVersion` is always null and
 * `updateAvailable` is always false, but `storedVersion` is still populated. */
export interface HapiTerminologyVersionCheckResult {
  code: string;
  supported: boolean;
  storedVersion: string | null;
  latestAvailableVersion: string | null;
  updateAvailable: boolean;
  errorMessage: string | null;
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

  /** Checks one system's official source for a newer version than what's stored locally. */
  scan(code: string): Observable<HapiTerminologyVersionCheckResult> {
    return this.http.post<HapiTerminologyVersionCheckResult>(HAPI_TERMINOLOGY_ENDPOINTS.scan(code), {});
  }

  /** Scans all 13 systems for a newer available version. */
  scanAll(): Observable<HapiTerminologyVersionCheckResult[]> {
    return this.http.post<HapiTerminologyVersionCheckResult[]>(HAPI_TERMINOLOGY_ENDPOINTS.scanAll, {});
  }

  /** Server-side paged, searchable browse of one system's locally stored codes ("View All Codes"). */
  getCodes(code: string, search: string | undefined, page: number, pageSize: number): Observable<PagedResult<TerminologyConcept>> {
    let params = new HttpParams().set('page', String(page)).set('pageSize', String(pageSize));
    if (search) params = params.set('search', search);
    return this.http.get<PagedResult<TerminologyConcept>>(HAPI_TERMINOLOGY_ENDPOINTS.codes(code), { params });
  }

  addCode(code: string, value: UpsertTerminologyConcept): Observable<TerminologyConcept> {
    return this.http.post<TerminologyConcept>(HAPI_TERMINOLOGY_ENDPOINTS.codes(code), value);
  }

  updateCode(code: string, pid: number, value: UpsertTerminologyConcept): Observable<TerminologyConcept> {
    return this.http.put<TerminologyConcept>(HAPI_TERMINOLOGY_ENDPOINTS.code(code, pid), value);
  }

  deleteCode(code: string, pid: number): Observable<void> {
    return this.http.delete<void>(HAPI_TERMINOLOGY_ENDPOINTS.code(code, pid));
  }
}
