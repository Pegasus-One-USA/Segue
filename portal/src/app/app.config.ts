import { ApplicationConfig, provideZoneChangeDetection, APP_INITIALIZER } from '@angular/core';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { provideAnimationsAsync } from '@angular/platform-browser/animations/async';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { routes } from './app.routes';
import { authInterceptor } from './auth/interceptors/auth.interceptor';
import { IAuthService } from './auth/services/i-auth.service';
import { IUserService } from './auth/services/i-user.service';
import { MockAuthService } from './auth/services/mock-auth.service';
import { ApiUserService } from './auth/services/api-user.service';
import { AuthService } from './auth/services/auth.service';

function initApp(auth: AuthService) {
  return () => auth.initFromToken();
}

export const appConfig: ApplicationConfig = {
  providers: [
    provideZoneChangeDetection({ eventCoalescing: true }),
    provideRouter(routes, withComponentInputBinding()),
    provideAnimationsAsync(),
    provideHttpClient(withInterceptors([authInterceptor])),

    // ── Mock ↔ Real swap point ────────────────────────────────────────────────
    // Auth (login) still runs against the mock until the auth backend is wired.
    { provide: IAuthService, useClass: MockAuthService },
    // User + Role management now hits the real backend at environment.apiBase.
    { provide: IUserService, useClass: ApiUserService },

    // ── Restore session on app start ──────────────────────────────────────────
    {
      provide: APP_INITIALIZER,
      useFactory: initApp,
      deps: [AuthService],
      multi: true,
    },
  ],
};
