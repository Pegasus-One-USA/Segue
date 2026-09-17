import { Injectable, inject, signal } from '@angular/core';
import { HttpClient, HttpContext } from '@angular/common/http';
import { catchError, of, tap, timeout } from 'rxjs';
import { SKIP_LOADER } from '../core/loading.interceptor';
import { VERSION_ENDPOINTS } from '../core/api-endpoints';
import { APP_VERSION } from '../version';

/** Wire shape of GET /api/v1/version — mirrors VersionController.VersionResponse field-for-field. */
interface VersionDto {
  version: string;
  commit: string;
  informationalVersion: string;
  buildDateUtc: string | null;
}

/**
 * Which build is running — surfaced in the footer, and in full in the About dialog.
 *
 * Two versions are tracked on purpose, because they can genuinely differ:
 *  - portalVersion  — baked into THIS bundle at container-build time (src/app/version.ts).
 *  - apiVersion     — reported by the backend that answered just now.
 * They match in any normal deployment, since both come from the same image tag. A mismatch is a
 * real and useful signal: a half-finished upgrade, or a browser holding a cached old bundle
 * against an already-upgraded API. Collapsing them into one number would hide exactly that.
 *
 * Never blocks anything: the API call is best-effort, and a failure leaves apiVersion null rather
 * than surfacing an error — an operator on a broken deployment still gets the portal version.
 */
@Injectable({ providedIn: 'root' })
export class VersionService {
  private readonly http = inject(HttpClient);

  /** Version of the portal bundle currently executing. Always known — it is compiled in. */
  readonly portalVersion = APP_VERSION;

  /** Version reported by the backend. Null until loaded, and stays null if the call fails. */
  readonly apiVersion = signal<VersionDto | null>(null);

  /** True once the API answered and its version differs from this bundle's — a stale cache or a
   *  partially-completed upgrade. Only meaningful for a real container build: a developer's
   *  'local' bundle never matches a versioned API and must not raise a false alarm. */
  readonly isMismatched = signal(false);

  load(): void {
    this.http
      .get<VersionDto>(VERSION_ENDPOINTS.get, {
        // Silent: this runs on app start, and a version probe must never flash the global loader
        // or hold up first paint.
        context: new HttpContext().set(SKIP_LOADER, true),
      })
      .pipe(
        timeout(5000),
        tap(dto => {
          this.apiVersion.set(dto);
          this.isMismatched.set(this.portalVersion !== 'local' && dto.version !== this.portalVersion);
        }),
        catchError(() => of(null)),
      )
      .subscribe();
  }
}
