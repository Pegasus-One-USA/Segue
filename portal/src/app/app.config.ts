import {
  ApplicationConfig,
  provideZoneChangeDetection,
  APP_INITIALIZER,
} from '@angular/core';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { provideAnimationsAsync } from '@angular/platform-browser/animations/async';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { routes } from './app.routes';
import { authInterceptor } from './auth/interceptors/auth.interceptor';
import { IAuthService } from './auth/services/i-auth.service';
import { IUserService } from './auth/services/i-user.service';
import { AuthApiService } from './auth/services/auth-api.service';
import { ApiUserService } from './auth/services/api-user.service';
import { AuthService } from './auth/services/auth.service';
import { AppInitService } from './onboarding/services/app-init.service';
import { IRoleService } from './user-management/services/i-role.service';
import { ApiRoleService } from './user-management/services/api-role.service';

function initApp(auth: AuthService, appInit: AppInitService) {
  // Restore any stored session, then resolve the first-run setup flag before routing starts.
  // checkSetup() is self-resilient (defaults requiresSetup=false if the API is unreachable),
  // so returning its Observable keeps the app booting even when the backend is down.
  return () => {
    auth.initFromToken();
    return appInit.checkSetup();
  };
}

export const appConfig: ApplicationConfig = {
  providers: [
    provideZoneChangeDetection({ eventCoalescing: true }),
    provideRouter(routes, withComponentInputBinding()),
    provideAnimationsAsync(),
    provideHttpClient(withInterceptors([authInterceptor])),

    // ── Real backend wiring (environment.apiBase) ────────────────────────────
    { provide: IAuthService, useClass: AuthApiService },
    { provide: IUserService, useClass: ApiUserService },
    { provide: IRoleService, useClass: ApiRoleService },

    // ── Restore session on app start ──────────────────────────────────────────
    {
      provide: APP_INITIALIZER,
      useFactory: initApp,
      deps: [AuthService, AppInitService],
      multi: true,
    },
  ],
};
