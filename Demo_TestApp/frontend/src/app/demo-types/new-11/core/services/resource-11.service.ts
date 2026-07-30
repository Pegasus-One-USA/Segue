import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../../../environments/environment';
import { Resource11MenuItem } from '../config/resource-11-menu.config';

const BACKEND_BASE_URL = environment.healthAppBase;

// Loads the rows for one "New 11" resource from its /api/v11/{apiSegment} endpoint. Rows come back as generic
// records (dynamic columns driven by resource-11-menu.config.ts), so the browser can render every resource type
// through one shared code path rather than a per-resource component.
@Injectable({ providedIn: 'root' })
export class Resource11Service {
  private readonly http = inject(HttpClient);

  getRows(menuItem: Resource11MenuItem): Observable<Record<string, unknown>[]> {
    return this.http.get<Record<string, unknown>[]>(
      `${BACKEND_BASE_URL}/api/v11/${menuItem.apiSegment}`,
      { withCredentials: true },
    );
  }
}
