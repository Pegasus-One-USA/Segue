import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpEvent, HttpRequest } from '@angular/common/http';
import { Observable } from 'rxjs';
import { SNOMED_ENDPOINTS } from '../../core/api-endpoints';

// The import runs in the background on the server (see TerminologyImportChannel) — the response to the
// upload POST only confirms the job was queued, never the eventual concept count. Poll getHistory() for that.
export interface ImportStartedResponse {
  message: string;
}

export interface SnomedConfiguration {
  hasApiKeyConfigured: boolean;
  schedulerEnabled: boolean;
  executionTime: string;
  retryCount: number;
  retryIntervalSeconds: number;
}
export type UpdateSnomedConfiguration = Omit<SnomedConfiguration, 'hasApiKeyConfigured'> & { apiKey?: string | null };

export interface SnomedImportHistoryEntry {
  id: string;
  version: string | null;
  startedOnUtc: string;
  completedOnUtc: string | null;
  importedConceptCount: number;
  status: string;
  errorMessage: string | null;
}

@Injectable({ providedIn: 'root' })
export class SnomedSettingsService {
  private readonly http = inject(HttpClient);

  get(): Observable<SnomedConfiguration> {
    return this.http.get<SnomedConfiguration>(SNOMED_ENDPOINTS.configuration);
  }

  update(value: UpdateSnomedConfiguration): Observable<SnomedConfiguration> {
    return this.http.put<SnomedConfiguration>(SNOMED_ENDPOINTS.configuration, value);
  }

  // Runs in the background on the server — this only confirms the sync job was queued.
  synchronize(): Observable<ImportStartedResponse> {
    return this.http.post<ImportStartedResponse>(SNOMED_ENDPOINTS.synchronize, {});
  }

  importFile(file: File): Observable<HttpEvent<ImportStartedResponse>> {
    const formData = new FormData();
    formData.append('file', file);
    const request = new HttpRequest('POST', SNOMED_ENDPOINTS.import, formData, { reportProgress: true });
    return this.http.request<ImportStartedResponse>(request);
  }

  getHistory(): Observable<SnomedImportHistoryEntry[]> {
    return this.http.get<SnomedImportHistoryEntry[]>(SNOMED_ENDPOINTS.history);
  }
}
