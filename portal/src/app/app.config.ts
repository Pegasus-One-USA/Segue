import { ApplicationConfig, provideZoneChangeDetection, APP_INITIALIZER } from '@angular/core';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { provideAnimationsAsync } from '@angular/platform-browser/animations/async';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { routes } from './app.routes';
import { authInterceptor } from './auth/interceptors/auth.interceptor';
import { IAuthService } from './auth/services/i-auth.service';
import { IUserService } from './auth/services/i-user.service';
import { AuthApiService } from './auth/services/auth-api.service';
import { ApiUserService } from './auth/services/api-user.service';
import { AuthService } from './auth/services/auth.service';
import { IRoleService } from './user-management/services/i-role.service';
import { ApiRoleService } from './user-management/services/api-role.service';

// initFromToken() now calls the real /me endpoint, so it's async — APP_INITIALIZER needs a Promise.
function initApp(auth: AuthService) {
  return () => firstValueFrom(auth.initFromToken());
}

export const appConfig: ApplicationConfig = {
  providers: [
    provideZoneChangeDetection({ eventCoalescing: true }),
    provideRouter(routes, withComponentInputBinding()),
    provideAnimationsAsync(),
    provideHttpClient(withInterceptors([authInterceptor])),

    // ── Mock ↔ Real swap point ────────────────────────────────────────────────
    // Auth (login) now hits the real backend at environment.apiBase.
    { provide: IAuthService, useClass: AuthApiService },
    // User + Role management now hits the real backend at environment.apiBase.
    { provide: IUserService, useClass: ApiUserService },
    { provide: IRoleService, useClass: ApiRoleService },

    // ── Restore session on app start ──────────────────────────────────────────
    {
      provide: APP_INITIALIZER,
      useFactory: initApp,
      deps: [AuthService],
      multi: true,
    },
  ],
};
