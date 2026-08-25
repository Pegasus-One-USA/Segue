import {
  ApplicationConfig,
  provideZoneChangeDetection,
  APP_INITIALIZER,
} from '@angular/core';
import { provideRouter, withComponentInputBinding, withRouterConfig, Router } from '@angular/router';
import { provideAnimationsAsync } from '@angular/platform-browser/animations/async';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { of, switchMap, firstValueFrom, tap } from 'rxjs';
import { routes } from './app.routes';
import { BrandingService } from './services/branding.service';
import { authInterceptor } from './auth/interceptors/auth.interceptor';
import { httpErrorSanitizerInterceptor } from './core/http-error-sanitizer.interceptor';
import { loadingInterceptor } from './core/loading.interceptor';
import { IAuthService } from './auth/services/i-auth.service';
import { IUserService } from './auth/services/i-user.service';
import { AuthApiService } from './auth/services/auth-api.service';
import { ApiUserService } from './auth/services/api-user.service';
import { AuthService } from './auth/services/auth.service';
import { AppInitService } from './onboarding/services/app-init.service';
import { SsoService } from './auth/services/sso.service';
import { SsoAuthApiService } from './auth/services/sso-auth-api.service';
import { IRoleService } from './user-management/services/i-role.service';
import { ApiRoleService } from './user-management/services/api-role.service';
import { IEhrEndpointService } from './ehr-endpoints/services/i-ehr-endpoint.service';
import { ApiEhrEndpointService } from './ehr-endpoints/services/api-ehr-endpoint.service';
import { ISourceConnectionService } from './source-connections/services/i-source-connection.service';
import { ApiSourceConnectionService } from './source-connections/services/api-source-connection.service';
import { IAllowedCorsOriginService } from './allowed-origins/services/i-allowed-cors-origin.service';
import { ApiAllowedCorsOriginService } from './allowed-origins/services/api-allowed-cors-origin.service';
import { ISystemSettingsService } from './system-settings/services/i-system-settings.service';
import { ApiSystemSettingsService } from './system-settings/services/api-system-settings.service';
import { IAppSecretsService } from './system-security/services/i-app-secrets.service';
import { ApiAppSecretsService } from './system-security/services/api-app-secrets.service';

function initApp(
  auth: AuthService,
  appInit: AppInitService,
  sso: SsoService,
  ssoAuthApi: SsoAuthApiService,
  router: Router,
  branding: BrandingService,
) {
  // Runs on EVERY app boot, including the one where the browser has just come back from Entra's
  // loginRedirect (see sso.service.ts) — handleRedirectResponse() resolves to null on a normal
  // boot, so this only branches when there's an actual response to process. Checked first, before
  // the usual setup/session-restore path below, since a fresh SSO login makes that path moot.
  async function completeEntraRedirectIfReturning(): Promise<boolean> {
    const redirectResult = await sso.handleRedirectResponse();
    if (!redirectResult) return false;

    try {
      await firstValueFrom(ssoAuthApi.ssoLogin(redirectResult.provider, redirectResult.token));
      void router.navigateByUrl('/dashboard');
    } catch {
      // Same "no matching account" possibility as any other SSO path — surfaced as a query param
      // since the login page isn't mounted yet at this point in the boot sequence to show a toast.
      void router.navigateByUrl('/auth/login?error=sso_failed');
    }
    return true;
  }

  // Resolve the first-run setup flag FIRST, then decide what to do with any stored session:
  //  • requiresSetup === true  → the backend has no users, so a lingering JWT is stale: discard it
  //    (otherwise authGuard would trust the old token and skip the first-run /setup screen).
  //  • requiresSetup === false → restore the stored session normally.
  // checkSetup() is self-resilient (defaults requiresSetup=false if the API is unreachable),
  // so the app still boots into the normal login flow when the backend is down.
  // HIPAA #7: initFromToken() now makes a real GET /auth/me call (the session cookie can't be
  // decoded client-side anymore) — it MUST be subscribed to, not just constructed and discarded,
  // or the app boots with no user ever restored. switchMap (not tap) is what actually does that.
  return async () => {
    // Resolved and applied HERE — as part of the initializer chain the router's initialNavigation
    // actually waits on — rather than fire-and-forget in BrandingService's own constructor. That used
    // to race every other startup path: nothing gated component creation on it, so a route (including
    // one landing directly on a refresh) could render, and read BrandingService.current(), before this
    // GET had completed — briefly, or in observed cases persistently, seeing DEFAULT_BRANDING instead
    // of the tenant's real saved configuration. Awaiting it here makes exactly one resolution the
    // authoritative one for this boot, with every component's first render guaranteed to see its
    // result. resolve() already falls back to DEFAULT_BRANDING internally on any network/server error
    // (see BrandingService), so this can never fail or hang boot indefinitely.
    await firstValueFrom(branding.resolve().pipe(tap(config => branding.applyToDocument(config))));

    const handledViaSso = await completeEntraRedirectIfReturning();
    if (handledViaSso) return;

    await firstValueFrom(
      appInit.checkSetup().pipe(
        switchMap(requiresSetup => {
          if (requiresSetup) {
            auth.discardSession();
            // Must still emit — firstValueFrom on the outer pipe throws EmptyError if this
            // inner observable completes without a value (an empty array does exactly that).
            return of(undefined);
          }
          return auth.initFromToken();
        }),
      ),
    );
  };
}

export const appConfig: ApplicationConfig = {
  providers: [
    provideZoneChangeDetection({ eventCoalescing: true }),
    // canceledNavigationResolution: 'computed' — when a CanDeactivate guard cancels a browser
    // Back/Forward-triggered navigation, this restores the actual history-stack position (via
    // history.go) instead of just the URL string, so Back/Forward depth stays consistent after
    // a declined "unsaved changes" prompt.
    provideRouter(routes, withComponentInputBinding(), withRouterConfig({ canceledNavigationResolution: 'computed' })),
    provideAnimationsAsync(),
    // Order matters: authInterceptor is the outer wrapper (closer to the app) so its 401
    // refresh-and-retry logic still sees the real status code; httpErrorSanitizerInterceptor is the
    // inner wrapper (closer to the network) so every error — including ones authInterceptor passes
    // through unchanged — has already had its unsafe `.message` replaced before anything reads it.
    // NOTE (Phase 6A): globalErrorInterceptor (auto friendly-error dialog) is intentionally NOT wired
    // here — it popped a blocking modal on every backend error, which interrupted workflow testing.
    // The backend still captures every exception with a reference id (Monitoring → Errors). Re-add it
    // gated to 5xx only if a global dialog is wanted.
    // loadingInterceptor runs outermost so it wraps every request/response as early/late as
    // possible, covering the full round-trip including auth's own refresh-and-retry calls.
    provideHttpClient(withInterceptors([loadingInterceptor, authInterceptor, httpErrorSanitizerInterceptor])),

    // ── Real backend wiring (environment.apiBase) ────────────────────────────
    { provide: IAuthService, useClass: AuthApiService },
    { provide: IUserService, useClass: ApiUserService },
    { provide: IRoleService, useClass: ApiRoleService },
    { provide: IEhrEndpointService, useClass: ApiEhrEndpointService },
    { provide: ISourceConnectionService, useClass: ApiSourceConnectionService },
    { provide: IAllowedCorsOriginService, useClass: ApiAllowedCorsOriginService },
    { provide: ISystemSettingsService, useClass: ApiSystemSettingsService },
    { provide: IAppSecretsService, useClass: ApiAppSecretsService },

    // ── Restore session on app start ──────────────────────────────────────────
    {
      provide: APP_INITIALIZER,
      useFactory: initApp,
      deps: [AuthService, AppInitService, SsoService, SsoAuthApiService, Router, BrandingService],
      multi: true,
    },
  ],
};
