import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, of, delay } from 'rxjs';
import { EPIC_ENV } from '../data/epic-environments.data';
import { EnvKey } from '../models/epic-env.model';

export interface DiscoveredEndpoints {
  token: string;
  authorize: string;
}

@Injectable({ providedIn: 'root' })
export class EpicDiscoveryService {
  private readonly http = inject(HttpClient);

  /**
   * Simulate or perform SMART discovery.
   * Returns discovered token + authorize endpoints after a short delay.
   */
  discover(baseUrl: string, envKey: EnvKey): Observable<DiscoveredEndpoints> {
    const env = EPIC_ENV[envKey];
    let token     = env.token;
    let authorize = env.authorize;

    if (envKey === 'production') {
      const host = this.extractHost(baseUrl);
      if (host) {
        token     = `https://${host}/interconnect/oauth2/token`;
        authorize = `https://${host}/interconnect/oauth2/authorize`;
      }
    }

    // Simulated 700 ms network delay (replace `of(...)` with actual HTTP call when ready)
    return of({ token, authorize }).pipe(delay(700));
  }

  private extractHost(url: string): string | null {
    try { return new URL(url).host; } catch { return null; }
  }
}
