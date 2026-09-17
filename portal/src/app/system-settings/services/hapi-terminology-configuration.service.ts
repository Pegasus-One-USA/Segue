import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams, HttpContext } from '@angular/common/http';
import { SKIP_LOADER } from '../../core/loading.interceptor';
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

  /** The app-wide loader dims and inerts the WHOLE screen for the duration of a request, which is the wrong
   *  shape of feedback for this table: it is one card among many on System Settings, and blanking the entire
   *  page for its own load made every other setting on the page unusable while it fetched. Worse, a sync in
   *  progress used to re-trigger that overlay every few seconds from its poll. Every call the table makes on
   *  its own behalf therefore opts out (SKIP_LOADER) and shows its own local state instead — skeleton rows
   *  while loading, a per-row spinner while a system is syncing. The dialogs reached from the ⋮ menu keep the
   *  global loader: those are modal anyway, so dimming the page behind them is correct. */
  private static silent(): HttpContext {
    return new HttpContext().set(SKIP_LOADER, true);
  }

  getAll(): Observable<HapiTerminologyConfiguration[]> {
    return this.http.get<HapiTerminologyConfiguration[]>(
      HAPI_TERMINOLOGY_ENDPOINTS.list, { context: HapiTerminologyConfigurationService.silent() });
  }

  get(code: string): Observable<HapiTerminologyConfiguration> {
    return this.http.get<HapiTerminologyConfiguration>(
      HAPI_TERMINOLOGY_ENDPOINTS.configuration(code), { context: HapiTerminologyConfigurationService.silent() });
  }

  update(code: string, value: UpdateHapiTerminologyConfiguration): Observable<HapiTerminologyConfiguration> {
    return this.http.put<HapiTerminologyConfiguration>(HAPI_TERMINOLOGY_ENDPOINTS.configuration(code), value);
  }

  // Runs in the background on the server — this only confirms the sync job was queued. Silent (see silent()):
  // the row's own spinner is the feedback, and blanking the page to say "we queued a job" is disproportionate.
  runNow(code: string): Observable<HapiTerminologyRunStartedResponse> {
    return this.http.post<HapiTerminologyRunStartedResponse>(
      HAPI_TERMINOLOGY_ENDPOINTS.runNow(code), {}, { context: HapiTerminologyConfigurationService.silent() });
  }

  /** Keeps the app-wide loader: its only caller is the History dialog, which is modal and has nothing else
   *  to show while it fetches. (The table used to poll this every 3 seconds for each running sync; that is
   *  gone — status now arrives over TerminologyStatusHubService.) */
  getHistory(code: string): Observable<TerminologyImportHistoryEntry[]> {
    return this.http.get<TerminologyImportHistoryEntry[]>(HAPI_TERMINOLOGY_ENDPOINTS.history(code));
  }

  /** Checks one system's official source for a newer version than what's stored locally. Silent (see
   *  silent()): the table fires 13 of these at once on load, each row showing its own "Checking…" state —
   *  letting them drive the app-wide loader would hold the whole page inert until the slowest one answered,
   *  which is exactly what firing them per row was meant to avoid. */
  scan(code: string): Observable<HapiTerminologyVersionCheckResult> {
    return this.http.post<HapiTerminologyVersionCheckResult>(
      HAPI_TERMINOLOGY_ENDPOINTS.scan(code), {}, { context: HapiTerminologyConfigurationService.silent() });
  }

  /** Scans all 13 systems for a newer available version. Silent (see silent()): this reaches 13 external
   *  sources and can take a while, and the "Scan for updates" button already reads "Scanning…" throughout —
   *  holding the whole page inert for the duration on top of that helps nobody. */
  scanAll(): Observable<HapiTerminologyVersionCheckResult[]> {
    return this.http.post<HapiTerminologyVersionCheckResult[]>(
      HAPI_TERMINOLOGY_ENDPOINTS.scanAll, {}, { context: HapiTerminologyConfigurationService.silent() });
  }

  /** Server-side paged, searchable browse of one system's locally stored codes ("View All Codes"). */
  /** `silent` comes only from the debounced search box: sends SKIP_LOADER so the app-wide loader
   *  (which dims the screen and inerts the content) never blurs the input being typed into. */
  getCodes(code: string, search: string | undefined, page: number, pageSize: number, silent = false): Observable<PagedResult<TerminologyConcept>> {
    let params = new HttpParams().set('page', String(page)).set('pageSize', String(pageSize));
    if (search) params = params.set('search', search);
    const context = silent ? new HttpContext().set(SKIP_LOADER, true) : undefined;
    return this.http.get<PagedResult<TerminologyConcept>>(HAPI_TERMINOLOGY_ENDPOINTS.codes(code), { params, context });
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
