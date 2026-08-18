import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpEvent, HttpRequest } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ICD10PCS_ENDPOINTS } from '../../core/api-endpoints';
import { ImportStartedResponse } from './snomed-settings.service';

export interface ReleaseFreshness {
  newerReleaseFound: boolean;
  latestKnownFileName: string | null;
  checkedOnUtc: string;
}

export interface Icd10PcsImportHistoryEntry {
  id: string;
  version: string | null;
  startedOnUtc: string;
  completedOnUtc: string | null;
  importedConceptCount: number;
  status: string;
  errorMessage: string | null;
}

@Injectable({ providedIn: 'root' })
export class Icd10PcsSettingsService {
  private readonly http = inject(HttpClient);

  getFreshness(): Observable<ReleaseFreshness | null> {
    return this.http.get<ReleaseFreshness | null>(ICD10PCS_ENDPOINTS.freshness);
  }

  checkForUpdates(): Observable<ReleaseFreshness> {
    return this.http.post<ReleaseFreshness>(ICD10PCS_ENDPOINTS.checkForUpdates, {});
  }

  downloadAndImport(): Observable<ImportStartedResponse> {
    return this.http.post<ImportStartedResponse>(ICD10PCS_ENDPOINTS.downloadAndImport, {});
  }

  importFile(file: File): Observable<HttpEvent<ImportStartedResponse>> {
    const formData = new FormData();
    formData.append('file', file);
    const request = new HttpRequest('POST', ICD10PCS_ENDPOINTS.import, formData, { reportProgress: true });
    return this.http.request<ImportStartedResponse>(request);
  }

  getHistory(): Observable<Icd10PcsImportHistoryEntry[]> {
    return this.http.get<Icd10PcsImportHistoryEntry[]>(ICD10PCS_ENDPOINTS.history);
  }
}
