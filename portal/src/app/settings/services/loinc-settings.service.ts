import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { LOINC_ENDPOINTS } from '../../core/api-endpoints';
import { ImportStartedResponse } from './snomed-settings.service';

export interface LoincConfiguration {
  downloadApiUrl: string; fhirApiUrl: string; hasUsernameConfigured: boolean; hasPasswordConfigured: boolean;
  schedulerEnabled: boolean; frequency: string; executionTime: string; retryCount: number; retryIntervalSeconds: number; downloadTimeoutSeconds: number;
}
export type UpdateLoincConfiguration = Omit<LoincConfiguration, 'hasUsernameConfigured' | 'hasPasswordConfigured'> & { username?: string | null; password?: string | null };

export interface LoincImportHistoryEntry {
  id: string;
  version: string | null;
  startedOnUtc: string;
  completedOnUtc: string | null;
  importedConceptCount: number;
  status: string;
  errorMessage: string | null;
}

@Injectable({ providedIn: 'root' })
export class LoincSettingsService {
  private readonly http = inject(HttpClient);
  get(): Observable<LoincConfiguration> { return this.http.get<LoincConfiguration>(LOINC_ENDPOINTS.configuration); }
  update(value: UpdateLoincConfiguration): Observable<LoincConfiguration> { return this.http.put<LoincConfiguration>(LOINC_ENDPOINTS.configuration, value); }
  // Runs in the background on the server now — this only confirms the sync job was queued.
  synchronize(): Observable<ImportStartedResponse> { return this.http.post<ImportStartedResponse>(LOINC_ENDPOINTS.synchronize, {}); }
  getHistory(): Observable<LoincImportHistoryEntry[]> { return this.http.get<LoincImportHistoryEntry[]>(LOINC_ENDPOINTS.history); }
}
