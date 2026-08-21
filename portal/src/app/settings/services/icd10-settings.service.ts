import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpEvent, HttpRequest } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ICD10_ENDPOINTS } from '../../core/api-endpoints';
import { ImportStartedResponse } from './snomed-settings.service';
import { ReleaseFreshness } from './icd10pcs-settings.service';

export interface Icd10ImportHistoryEntry {
  id: string;
  version: string | null;
  startedOnUtc: string;
  completedOnUtc: string | null;
  importedCodeCount: number;
  status: string;
  errorMessage: string | null;
}

@Injectable({ providedIn: 'root' })
export class Icd10SettingsService {
  private readonly http = inject(HttpClient);

  getFreshness(): Observable<ReleaseFreshness | null> {
    return this.http.get<ReleaseFreshness | null>(ICD10_ENDPOINTS.freshness);
  }

  checkForUpdates(): Observable<ReleaseFreshness> {
    return this.http.post<ReleaseFreshness>(ICD10_ENDPOINTS.checkForUpdates, {});
  }

  downloadAndImport(): Observable<ImportStartedResponse> {
    return this.http.post<ImportStartedResponse>(ICD10_ENDPOINTS.downloadAndImport, {});
  }

  importFile(file: File): Observable<HttpEvent<ImportStartedResponse>> {
    const formData = new FormData();
    formData.append('file', file);
    const request = new HttpRequest('POST', ICD10_ENDPOINTS.import, formData, { reportProgress: true });
    return this.http.request<ImportStartedResponse>(request);
  }

  getHistory(): Observable<Icd10ImportHistoryEntry[]> {
    return this.http.get<Icd10ImportHistoryEntry[]>(ICD10_ENDPOINTS.history);
  }
}
