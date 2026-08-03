import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, throwError } from 'rxjs';
import { catchError } from 'rxjs/operators';
import { APP_SECRETS_ENDPOINTS } from '../../core/api-endpoints';
import { IAppSecretsService } from './i-app-secrets.service';
import { AppSecret } from '../models/app-secret.model';

@Injectable({ providedIn: 'root' })
export class ApiAppSecretsService extends IAppSecretsService {
  private readonly http = inject(HttpClient);

  getAll(): Observable<AppSecret[]> {
    return this.http.get<AppSecret[]>(APP_SECRETS_ENDPOINTS.list).pipe(
      catchError(err => throwError(() => err))
    );
  }

  regenerate(secretName: string): Observable<AppSecret> {
    return this.http.post<AppSecret>(APP_SECRETS_ENDPOINTS.regenerate(secretName), {}).pipe(
      catchError(err => throwError(() => err))
    );
  }
}
