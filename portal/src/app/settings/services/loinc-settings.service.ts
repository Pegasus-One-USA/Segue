import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { LOINC_ENDPOINTS } from '../../core/api-endpoints';

export interface LoincConfiguration {
  downloadApiUrl: string; fhirApiUrl: string; hasUsernameConfigured: boolean; hasPasswordConfigured: boolean;
  schedulerEnabled: boolean; frequency: string; executionTime: string; retryCount: number; retryIntervalSeconds: number; downloadTimeoutSeconds: number;
}
export type UpdateLoincConfiguration = Omit<LoincConfiguration, 'hasUsernameConfigured' | 'hasPasswordConfigured'> & { username?: string | null; password?: string | null };
@Injectable({ providedIn: 'root' })
export class LoincSettingsService {
  private readonly http = inject(HttpClient);
  get(): Observable<LoincConfiguration> { return this.http.get<LoincConfiguration>(LOINC_ENDPOINTS.configuration); }
  update(value: UpdateLoincConfiguration): Observable<LoincConfiguration> { return this.http.put<LoincConfiguration>(LOINC_ENDPOINTS.configuration, value); }
  synchronize(): Observable<{ version: string; importedConceptCount: number; alreadyCurrent: boolean }> { return this.http.post<{ version: string; importedConceptCount: number; alreadyCurrent: boolean }>(LOINC_ENDPOINTS.synchronize, {}); }
}
