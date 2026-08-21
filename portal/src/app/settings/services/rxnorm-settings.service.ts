import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpEvent, HttpRequest } from '@angular/common/http';
import { Observable } from 'rxjs';
import { RXNORM_ENDPOINTS } from '../../core/api-endpoints';
import { ImportStartedResponse } from './snomed-settings.service';

export interface RxNormConfiguration {
  hasApiKeyConfigured: boolean;
  schedulerEnabled: boolean;
  executionTime: string;
  retryCount: number;
  retryIntervalSeconds: number;
}
export type UpdateRxNormConfiguration = Omit<RxNormConfiguration, 'hasApiKeyConfigured'> & { apiKey?: string | null };

export interface RxNormImportHistoryEntry {
  id: string;
  version: string | null;
  startedOnUtc: string;
  completedOnUtc: string | null;
  importedConceptCount: number;
  status: string;
  errorMessage: string | null;
}

@Injectable({ providedIn: 'root' })
export class RxNormSettingsService {
  private readonly http = inject(HttpClient);

  get(): Observable<RxNormConfiguration> {
    return this.http.get<RxNormConfiguration>(RXNORM_ENDPOINTS.configuration);
  }

  update(value: UpdateRxNormConfiguration): Observable<RxNormConfiguration> {
    return this.http.put<RxNormConfiguration>(RXNORM_ENDPOINTS.configuration, value);
  }

  // Runs in the background on the server — this only confirms the sync job was queued.
  synchronize(): Observable<ImportStartedResponse> {
    return this.http.post<ImportStartedResponse>(RXNORM_ENDPOINTS.synchronize, {});
  }

  importFile(file: File): Observable<HttpEvent<ImportStartedResponse>> {
    const formData = new FormData();
    formData.append('file', file);
    const request = new HttpRequest('POST', RXNORM_ENDPOINTS.import, formData, { reportProgress: true });
    return this.http.request<ImportStartedResponse>(request);
  }

  getHistory(): Observable<RxNormImportHistoryEntry[]> {
    return this.http.get<RxNormImportHistoryEntry[]>(RXNORM_ENDPOINTS.history);
  }
}
