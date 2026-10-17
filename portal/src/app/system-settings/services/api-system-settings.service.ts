import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, throwError } from 'rxjs';
import { catchError, tap } from 'rxjs/operators';
import { SYSTEM_SETTINGS_ENDPOINTS } from '../../core/api-endpoints';
import { EhrWriteCapabilitiesService } from '../../services/ehr-write-capabilities.service';
import { BatchSystemSettingItem, ISystemSettingsService } from './i-system-settings.service';
import { DecryptProvisionedSecretResponse, SetSystemSettingRequest, SystemSetting } from '../models/system-setting.model';

@Injectable({ providedIn: 'root' })
export class ApiSystemSettingsService extends ISystemSettingsService {
  private readonly http = inject(HttpClient);
  // Its answers carry system settings (EhrWriteBack:DryRunEnabled, CloneModeEnabled): drop them after any change.
  private readonly ehrWriteCapabilities = inject(EhrWriteCapabilitiesService);

  getAll(): Observable<SystemSetting[]> {
    return this.http.get<SystemSetting[]>(SYSTEM_SETTINGS_ENDPOINTS.list).pipe(
      catchError(err => throwError(() => err))
    );
  }

  set(key: string, req: SetSystemSettingRequest): Observable<SystemSetting> {
    return this.http.put<SystemSetting>(SYSTEM_SETTINGS_ENDPOINTS.byKey(key), req).pipe(
      tap(() => this.ehrWriteCapabilities.clear()),
      catchError(err => throwError(() => err))
    );
  }

  setBatch(items: BatchSystemSettingItem[]): Observable<SystemSetting[]> {
    return this.http.put<SystemSetting[]>(SYSTEM_SETTINGS_ENDPOINTS.batch, { items }).pipe(
      tap(() => this.ehrWriteCapabilities.clear()),
      catchError(err => throwError(() => err))
    );
  }

  delete(key: string): Observable<void> {
    return this.http.delete<void>(SYSTEM_SETTINGS_ENDPOINTS.byKey(key)).pipe(
      tap(() => this.ehrWriteCapabilities.clear()),
      catchError(err => throwError(() => err))
    );
  }

  decryptProvisionedSecret(protectedValue: string): Observable<DecryptProvisionedSecretResponse> {
    return this.http.post<DecryptProvisionedSecretResponse>(
      SYSTEM_SETTINGS_ENDPOINTS.decryptProvisionedSecret,
      { protectedValue }
    ).pipe(
      catchError(err => throwError(() => err))
    );
  }
}
