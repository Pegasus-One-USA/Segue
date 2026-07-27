import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, throwError } from 'rxjs';
import { catchError } from 'rxjs/operators';
import { SYSTEM_SETTINGS_ENDPOINTS } from '../../core/api-endpoints';
import { ISystemSettingsService } from './i-system-settings.service';
import { SetSystemSettingRequest, SystemSetting } from '../models/system-setting.model';

@Injectable({ providedIn: 'root' })
export class ApiSystemSettingsService extends ISystemSettingsService {
  private readonly http = inject(HttpClient);

  getAll(): Observable<SystemSetting[]> {
    return this.http.get<SystemSetting[]>(SYSTEM_SETTINGS_ENDPOINTS.list).pipe(
      catchError(err => throwError(() => err))
    );
  }

  set(key: string, req: SetSystemSettingRequest): Observable<SystemSetting> {
    return this.http.put<SystemSetting>(SYSTEM_SETTINGS_ENDPOINTS.byKey(key), req).pipe(
      catchError(err => throwError(() => err))
    );
  }

  delete(key: string): Observable<void> {
    return this.http.delete<void>(SYSTEM_SETTINGS_ENDPOINTS.byKey(key)).pipe(
      catchError(err => throwError(() => err))
    );
  }
}
