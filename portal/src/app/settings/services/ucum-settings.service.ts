import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpEvent, HttpRequest } from '@angular/common/http';
import { Observable } from 'rxjs';
import { UCUM_ENDPOINTS } from '../../core/api-endpoints';
import { ImportStartedResponse } from './snomed-settings.service';

export interface UcumConfiguration {
  schedulerEnabled: boolean;
  frequency: string;
  executionTime: string;
}

export interface UcumImportHistoryEntry {
  id: string;
  version: string | null;
  startedOnUtc: string;
  completedOnUtc: string | null;
  importedConceptCount: number;
  status: string;
  errorMessage: string | null;
}

@Injectable({ providedIn: 'root' })
export class UcumSettingsService {
  private readonly http = inject(HttpClient);

  get(): Observable<UcumConfiguration> {
    return this.http.get<UcumConfiguration>(UCUM_ENDPOINTS.configuration);
  }

  update(value: UcumConfiguration): Observable<UcumConfiguration> {
    return this.http.put<UcumConfiguration>(UCUM_ENDPOINTS.configuration, value);
  }

  synchronize(): Observable<ImportStartedResponse> {
    return this.http.post<ImportStartedResponse>(UCUM_ENDPOINTS.synchronize, {});
  }

  importFile(file: File): Observable<HttpEvent<ImportStartedResponse>> {
    const formData = new FormData();
    formData.append('file', file);
    const request = new HttpRequest('POST', UCUM_ENDPOINTS.import, formData, { reportProgress: true });
    return this.http.request<ImportStartedResponse>(request);
  }

  getHistory(): Observable<UcumImportHistoryEntry[]> {
    return this.http.get<UcumImportHistoryEntry[]>(UCUM_ENDPOINTS.history);
  }
}
