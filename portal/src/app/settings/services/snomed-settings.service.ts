import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpEvent, HttpRequest } from '@angular/common/http';
import { Observable } from 'rxjs';
import { SNOMED_ENDPOINTS } from '../../core/api-endpoints';

export interface SnomedImportResult {
  version: string;
  importedConceptCount: number;
}

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

  importFile(file: File): Observable<HttpEvent<SnomedImportResult>> {
    const formData = new FormData();
    formData.append('file', file);
    const request = new HttpRequest('POST', SNOMED_ENDPOINTS.import, formData, { reportProgress: true });
    return this.http.request<SnomedImportResult>(request);
  }

  getHistory(): Observable<SnomedImportHistoryEntry[]> {
    return this.http.get<SnomedImportHistoryEntry[]>(SNOMED_ENDPOINTS.history);
  }
}
