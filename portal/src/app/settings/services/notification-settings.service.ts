import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { NOTIFICATION_SETTINGS_ENDPOINTS } from '../../core/api-endpoints';
import { NotificationSettingsModel, UpdateNotificationSettingsRequest } from '../models/notification-settings.model';

@Injectable({ providedIn: 'root' })
export class NotificationSettingsService {
  private readonly http = inject(HttpClient);

  get(): Observable<NotificationSettingsModel> {
    return this.http.get<NotificationSettingsModel>(NOTIFICATION_SETTINGS_ENDPOINTS.get);
  }

  update(request: UpdateNotificationSettingsRequest): Observable<NotificationSettingsModel> {
    return this.http.put<NotificationSettingsModel>(NOTIFICATION_SETTINGS_ENDPOINTS.update, request);
  }

  testSend(toEmail: string): Observable<void> {
    return this.http.post<void>(NOTIFICATION_SETTINGS_ENDPOINTS.testSend, { toEmail });
  }
}
