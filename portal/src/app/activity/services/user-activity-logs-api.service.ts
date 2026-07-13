import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { USER_ACTIVITY_LOGS_ENDPOINTS } from '../../core/api-endpoints';
import { PagedResult, UserActivityLog, UserActivityLogFilter } from '../models/user-activity-log.model';

@Injectable({ providedIn: 'root' })
export class UserActivityLogsApiService {
  private readonly http = inject(HttpClient);

  list(filter: UserActivityLogFilter): Observable<PagedResult<UserActivityLog>> {
    let params = new HttpParams()
      .set('page', filter.page)
      .set('pageSize', filter.pageSize);

    if (filter.category) params = params.set('category', filter.category);
    if (filter.status) params = params.set('status', filter.status);
    if (filter.userId) params = params.set('userId', filter.userId);
    if (filter.search) params = params.set('search', filter.search);

    return this.http.get<PagedResult<UserActivityLog>>(USER_ACTIVITY_LOGS_ENDPOINTS.list, { params });
  }
}
