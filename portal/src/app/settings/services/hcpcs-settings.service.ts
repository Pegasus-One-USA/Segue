import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpEvent, HttpRequest } from '@angular/common/http';
import { Observable } from 'rxjs';
import { HCPCS_ENDPOINTS } from '../../core/api-endpoints';
import { ImportStartedResponse } from './snomed-settings.service';
import { ReleaseFreshness } from './icd10pcs-settings.service';

export interface HcpcsImportHistoryEntry {
  id: string;
  version: string | null;
  startedOnUtc: string;
  completedOnUtc: string | null;
  importedConceptCount: number;
  status: string;
  errorMessage: string | null;
}

@Injectable({ providedIn: 'root' })
export class HcpcsSettingsService {
  private readonly http = inject(HttpClient);

  getFreshness(): Observable<ReleaseFreshness | null> {
    return this.http.get<ReleaseFreshness | null>(HCPCS_ENDPOINTS.freshness);
  }

  checkForUpdates(): Observable<ReleaseFreshness> {
    return this.http.post<ReleaseFreshness>(HCPCS_ENDPOINTS.checkForUpdates, {});
  }

  downloadAndImport(): Observable<ImportStartedResponse> {
    return this.http.post<ImportStartedResponse>(HCPCS_ENDPOINTS.downloadAndImport, {});
  }

  importFile(file: File): Observable<HttpEvent<ImportStartedResponse>> {
    const formData = new FormData();
    formData.append('file', file);
    const request = new HttpRequest('POST', HCPCS_ENDPOINTS.import, formData, { reportProgress: true });
    return this.http.request<ImportStartedResponse>(request);
  }

  getHistory(): Observable<HcpcsImportHistoryEntry[]> {
    return this.http.get<HcpcsImportHistoryEntry[]>(HCPCS_ENDPOINTS.history);
  }
}
