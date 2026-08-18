import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { SSO_CONFIGURATIONS_ENDPOINTS } from '../../core/api-endpoints';
import { SsoConfigurationsModel, UpdateSsoConfigurationsRequest } from '../models/sso-configurations.model';

@Injectable({ providedIn: 'root' })
export class SsoConfigurationsService {
  private readonly http = inject(HttpClient);

  get(): Observable<SsoConfigurationsModel> {
    return this.http.get<SsoConfigurationsModel>(SSO_CONFIGURATIONS_ENDPOINTS.get);
  }

  update(request: UpdateSsoConfigurationsRequest): Observable<SsoConfigurationsModel> {
    return this.http.put<SsoConfigurationsModel>(SSO_CONFIGURATIONS_ENDPOINTS.update, request);
  }
}
