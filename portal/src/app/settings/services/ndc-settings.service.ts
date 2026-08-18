import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpEvent, HttpRequest } from '@angular/common/http';
import { Observable } from 'rxjs';
import { NDC_ENDPOINTS } from '../../core/api-endpoints';
import { ImportStartedResponse } from './snomed-settings.service';

export interface NdcConfiguration {
  hasApiKeyConfigured: boolean;
  schedulerEnabled: boolean;
  frequency: string;
  executionTime: string;
}
export type UpdateNdcConfiguration = Omit<NdcConfiguration, 'hasApiKeyConfigured'> & { apiKey?: string | null };

export interface NdcImportHistoryEntry {
  id: string;
  version: string | null;
  startedOnUtc: string;
  completedOnUtc: string | null;
  importedConceptCount: number;
  status: string;
  errorMessage: string | null;
}

@Injectable({ providedIn: 'root' })
export class NdcSettingsService {
  private readonly http = inject(HttpClient);

  get(): Observable<NdcConfiguration> {
    return this.http.get<NdcConfiguration>(NDC_ENDPOINTS.configuration);
  }

  update(value: UpdateNdcConfiguration): Observable<NdcConfiguration> {
    return this.http.put<NdcConfiguration>(NDC_ENDPOINTS.configuration, value);
  }

  synchronize(): Observable<ImportStartedResponse> {
    return this.http.post<ImportStartedResponse>(NDC_ENDPOINTS.synchronize, {});
  }

  importFile(file: File): Observable<HttpEvent<ImportStartedResponse>> {
    const formData = new FormData();
    formData.append('file', file);
    const request = new HttpRequest('POST', NDC_ENDPOINTS.import, formData, { reportProgress: true });
    return this.http.request<ImportStartedResponse>(request);
  }

  getHistory(): Observable<NdcImportHistoryEntry[]> {
    return this.http.get<NdcImportHistoryEntry[]>(NDC_ENDPOINTS.history);
  }
}
