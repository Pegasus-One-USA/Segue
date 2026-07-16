import {
  ApplicationConfig,
  provideZoneChangeDetection,
  APP_INITIALIZER,
} from '@angular/core';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { provideAnimationsAsync } from '@angular/platform-browser/animations/async';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { tap } from 'rxjs';
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
import { IEhrEndpointService } from './ehr-endpoints/services/i-ehr-endpoint.service';
import { ApiEhrEndpointService } from './ehr-endpoints/services/api-ehr-endpoint.service';
import { ISourceConnectionService } from './source-connections/services/i-source-connection.service';
import { ApiSourceConnectionService } from './source-connections/services/api-source-connection.service';

function initApp(auth: AuthService, appInit: AppInitService) {
  // Resolve the first-run setup flag FIRST, then decide what to do with any stored session:
  //  • requiresSetup === true  → the backend has no users, so a lingering JWT is stale: discard it
  //    (otherwise authGuard would trust the old token and skip the first-run /setup screen).
  //  • requiresSetup === false → restore the stored session normally.
  // checkSetup() is self-resilient (defaults requiresSetup=false if the API is unreachable),
  // so the app still boots into the normal login flow when the backend is down.
  return () =>
    appInit.checkSetup().pipe(
      tap(requiresSetup => (requiresSetup ? auth.discardSession() : auth.initFromToken())),
    );
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
    { provide: IEhrEndpointService, useClass: ApiEhrEndpointService },
    { provide: ISourceConnectionService, useClass: ApiSourceConnectionService },

    // ── Restore session on app start ──────────────────────────────────────────
    {
      provide: APP_INITIALIZER,
      useFactory: initApp,
      deps: [AuthService, AppInitService],
      multi: true,
    },
  ],
};
