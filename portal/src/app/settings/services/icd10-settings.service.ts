import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpEvent, HttpRequest } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ICD10_ENDPOINTS } from '../../core/api-endpoints';

export interface Icd10ImportResult {
  version: string;
  importedCodeCount: number;
}

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

  importFile(file: File): Observable<HttpEvent<Icd10ImportResult>> {
    const formData = new FormData();
    formData.append('file', file);
    const request = new HttpRequest('POST', ICD10_ENDPOINTS.import, formData, { reportProgress: true });
    return this.http.request<Icd10ImportResult>(request);
  }

  getHistory(): Observable<Icd10ImportHistoryEntry[]> {
    return this.http.get<Icd10ImportHistoryEntry[]>(ICD10_ENDPOINTS.history);
  }
}
