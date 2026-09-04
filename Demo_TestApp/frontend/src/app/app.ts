import { Component, OnInit, computed, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { LaunchProviderInAppComponent } from './demo-types/demo-type-2/launch-provider-in-app';
import { LaunchStandalonePatientComponent } from './demo-types/patient-standalone/launch-standalone-patient';
import { AdminSettingsComponent } from './demo-types/admin-settings/admin-settings';
// Per-role "Default | New 11" shells. Each wraps its role's existing screen (Default) plus the shared curated-_11
// browser (New 11); the existing screens are imported by the wrappers, not here, and are otherwise untouched.
import { PatientNew11Component } from './demo-types/new-11/patient-new11';
import { ProviderStandaloneNew11Component } from './demo-types/new-11/provider-standalone-new11';
import { ProviderInAppNew11Component } from './demo-types/new-11/provider-in-app-new11';
import { BackendSystemNew11Component } from './demo-types/new-11/backend-system-new11';
import { environment } from '../environments/environment';
import { AUTH_EMAIL_STORAGE_KEY, PATIENT_STANDALONE_PATH } from './core/routes';

const BACKEND_BASE_URL = environment.healthAppBase;

// Embedded EHR launches round-trip this tab through FHIRBridge + Epic and back to this same origin via a full
// top-level navigation *inside the iframe* Epic embeds this app in (see LaunchProviderInAppComponent.ngOnInit).
// hb_session is SameSite=Lax with no Secure flag, so on that return leg the browser won't send it back — Lax
// cookies are excluded from requests made in a cross-site iframe context, even though the navigation looks
// top-level from inside the frame. That made restoreSession()'s cookie check fail after every embedded launch,
// re-showing the login screen and forcing a second login for a session that was already established. sessionStorage
// isn't subject to that restriction (it's plain per-origin storage, not a cookie sent on requests), so mirror the
// logged-in role there at login and trust it first on restore — sidesteps the blocked cookie read entirely.
const AUTH_ROLE_STORAGE_KEY = 'hb_auth_role';

// A direct EHR-launch redirect (Epic calling this app's registered launch URL) lands on this exact path with
// ?iss=&launch= already on the URL. This is a URL-entry-point concern, independent of which Role eventually logs
// in here — Epic gives no login context at all, so the app can't wait to learn a role before deciding to hand off
// to LaunchProviderInAppComponent. isProviderInAppLaunch (below) captures that "wins regardless of role" override.
const PROVIDER_IN_APP_PATH = '/launchproviderinapp';

// True SMART Standalone Launch (provider-initiated, not EHR-initiated): reached only by a ProviderStandalone-role
// login. Unlike PROVIDER_IN_APP_PATH above, there's no incoming iss/launch to detect pre-login — the redirect in
// login() below is purely "send this role to its own screen after login," so a reload lands back on the right
// component.
const PROVIDER_STANDALONE_PATH = '/launchinstandaloneprovider';

// BackendSystem-role login target — mirrors PROVIDER_IN_APP_PATH/PROVIDER_STANDALONE_PATH's "send this role to
// its own screen after login" redirect below. Unlike those two, there's no external EHR-launch entry point to
// also detect pre-login; this role only ever reaches its screen via the redirect in login().
const BACKEND_SYSTEM_PATH = '/backend-system';

// Each role's default post-login route. login() sends the user here on sign-in, regardless of which path the login
// form was served from. Patient/Admin default to the app root (their screens render at '/'); the launch/backend
// roles have their own dedicated paths. Keys are the backend's role strings (see HealthAppDbContext.cs).
const ROLE_DEFAULT_PATHS: Record<string, string> = {
  Admin: '/',
  Patient: '/',
  ProviderStandalone: PROVIDER_STANDALONE_PATH,
  ProviderInApp: PROVIDER_IN_APP_PATH,
  BackendSystem: BACKEND_SYSTEM_PATH,
};

// Patient login does NOT redirect to PATIENT_STANDALONE_PATH the way the two roles above redirect — it lands on
// the demo-type-1 dashboard mockup like Admin does, and only reaches this path (and therefore
// LaunchStandalonePatientComponent's real hospital-picker/OAuth flow) via that dashboard's own "Connect Get Data"
// button doing a full-page navigation to PATIENT_STANDALONE_PATH (see PatientStandaloneComponent.openConnectFlow).
// isOnPatientStandaloneLaunchPath (below) is what lets app.html tell the two situations apart once role() is
// 'Patient' either way.

// Friendly display labels for the header badge — purely cosmetic, derived from the authenticated role. Kept so
// the on-screen badge text matches what it always has, even though there's no backing DemoType row anymore.
const ROLE_DISPLAY_NAMES: Record<string, string> = {
  Admin: 'Admin',
  Patient: 'Patient_Standalone',
  ProviderStandalone: 'Provider_Standalone',
  ProviderInApp: 'Provider_InApp',
  BackendSystem: 'Backend_System',
};

// The backend seeds four roles (Admin, Patient, ProviderStandalone, ProviderInApp — see HealthAppDbContext.cs) —
// this app only ever does an `=== 'Admin'`-style string comparison against it, so there's no real union to
// enumerate here.
interface LoginResponse {
  email: string;
  role: string;
}

@Component({
  selector: 'app-root',
  imports: [
    FormsModule,
    LaunchProviderInAppComponent,
    LaunchStandalonePatientComponent,
    AdminSettingsComponent,
    PatientNew11Component,
    ProviderStandaloneNew11Component,
    ProviderInAppNew11Component,
    BackendSystemNew11Component,
  ],
  templateUrl: './app.html',
  styleUrl: './app.scss'
})
export class App implements OnInit {
  protected readonly loggedIn = signal(false);
  protected readonly role = signal<string | null>(null);
  protected readonly loginEmail = signal('');
  protected readonly loginPassword = signal('');
  protected readonly loginError = signal('');

  protected readonly loginTypeLabel = computed(() => {
    const role = this.role();
    return role ? (ROLE_DISPLAY_NAMES[role] ?? role) : '';
  });

  // Evaluated once at boot: this app never uses a real <router-outlet> (navigation is always a full page load via
  // window.location.href), so the path/query can't change out from under a live component instance — a plain field
  // is enough, no need for a signal. True when this page load is an EHR-launch entry point (either the dedicated
  // path, or Epic's iss/launch params landing on some other path), regardless of the eventual authenticated role.
  protected readonly isProviderInAppLaunch = (() => {
    const params = new URLSearchParams(window.location.search);
    return window.location.pathname.toLowerCase() === PROVIDER_IN_APP_PATH
      || (!!params.get('iss') && !!params.get('launch'));
  })();

  // A LIVE Epic EHR launch specifically — iss + launch are on the URL. This is the only case that must bypass the
  // new "Default | New 11" menu and hand straight off to LaunchProviderInAppComponent (its ngOnInit redirects to
  // FHIRBridge). A plain ProviderInApp login (which login() still routes to PROVIDER_IN_APP_PATH, but with no
  // iss/launch) falls through to the ProviderInApp menu shell instead, so that role also gets its New 11 view.
  protected readonly isRealEhrLaunch = (() => {
    const params = new URLSearchParams(window.location.search);
    return !!params.get('iss') && !!params.get('launch');
  })();

  // The WHOLE ProviderInApp EHR-launch round-trip must render LaunchProviderInAppComponent BEFORE (and instead of)
  // the login gate, not after it — an embedded Epic/eCW launch runs this app inside a cross-site iframe where the
  // hb_session cookie (SameSite=Lax) is never sent, so loggedIn() is always false there and the old login-first
  // ordering trapped every embedded launch on the sign-in screen, so its ngOnInit hand-off to FHIRBridge (and the
  // iframe break-out) never ran. Both legs land on PROVIDER_IN_APP_PATH: the inbound leg carries iss+launch, the
  // post-OAuth return leg carries workflowRunId (or launchError). A plain ProviderInApp login carries none of
  // these, so it still falls through to that role's New 11 menu, gated on login as before.
  protected readonly isProviderInAppLaunchFlow = (() => {
    const params = new URLSearchParams(window.location.search);
    const onLaunchPath = window.location.pathname.toLowerCase() === PROVIDER_IN_APP_PATH;
    const hasLaunchParams = (!!params.get('iss') && !!params.get('launch'))
      || !!params.get('workflowRunId')
      || !!params.get('launchError')
      // ?autofetch=1 opens the patient view directly (no launch), where it auto-fetches the workflow's latest
      // patient by workflow id — lets a user re-view an already-launched patient without a fresh EHR launch.
      || !!params.get('autofetch');
    return onLaunchPath && hasLaunchParams;
  })();

  // True only when this exact page load landed on PATIENT_STANDALONE_PATH (i.e. via the dashboard mockup's
  // "Connect Get Data" redirect), as opposed to a fresh Patient login that should show the dashboard mockup instead.
  protected readonly isOnPatientStandaloneLaunchPath = window.location.pathname.toLowerCase() === PATIENT_STANDALONE_PATH;

  constructor(private readonly http: HttpClient) {}

  ngOnInit(): void {
    // The OAuth "Connect Get Data" flow opens the workflow URL in a new tab; when it redirects back the
    // Angular app boots from scratch in that tab. Re-check the hb_session cookie (shared across tabs, unlike
    // this component's signals) so an already-logged-in user lands on the dashboard instead of the login screen.
    void this.restoreSession();
  }

  private async restoreSession(): Promise<void> {
    const storedRole = sessionStorage.getItem(AUTH_ROLE_STORAGE_KEY);
    if (storedRole) {
      this.role.set(storedRole);
      this.loggedIn.set(true);
      return;
    }

    try {
      const response = await firstValueFrom(
        this.http.get<LoginResponse>(`${BACKEND_BASE_URL}/api/session`, { withCredentials: true })
      );
      this.role.set(response.role);
      this.loggedIn.set(true);
      sessionStorage.setItem(AUTH_ROLE_STORAGE_KEY, response.role);
      sessionStorage.setItem(AUTH_EMAIL_STORAGE_KEY, response.email);
    } catch {
      // No valid session cookie — stay on the login screen.
    }
  }

  async login(): Promise<void> {
    this.loginError.set('');

    try {
      const response = await firstValueFrom(
        this.http.post<LoginResponse>(
          `${BACKEND_BASE_URL}/api/login`,
          { email: this.loginEmail(), password: this.loginPassword() },
          { withCredentials: true }
        )
      );

      this.role.set(response.role);
      this.loggedIn.set(true);
      sessionStorage.setItem(AUTH_ROLE_STORAGE_KEY, response.role);
      sessionStorage.setItem(AUTH_EMAIL_STORAGE_KEY, response.email);

      // Send every role to its own default route on sign-in, regardless of which path the login form was served
      // from (after logout that's always '/', but a direct deep link or an Epic launch URL can differ).
      // ProviderInApp keeps the query string so a live Epic launch (?iss=&launch=) still reaches
      // LaunchProviderInAppComponent's ngOnInit hand-off; the other roles move to a clean URL. When the user is
      // already on the right path, no navigation happens and the signals set above render the role in place.
      const defaultPath = ROLE_DEFAULT_PATHS[response.role] ?? '/';
      const targetSearch = response.role === 'ProviderInApp' ? window.location.search : '';
      if (window.location.pathname.toLowerCase() !== defaultPath || window.location.search !== targetSearch) {
        window.location.href = defaultPath + targetSearch;
      }
    } catch {
      this.loginError.set('Invalid email or password.');
    }
  }

  async logout(): Promise<void> {
    try {
      await firstValueFrom(this.http.post(`${BACKEND_BASE_URL}/api/logout`, {}, { withCredentials: true }));
    } catch {
      // Non-fatal — even if the backend is unreachable (or the session was already gone server-side), the user
      // still needs to be logged out of this app locally rather than stuck on a dead "logged in" screen.
    }
    sessionStorage.removeItem(AUTH_ROLE_STORAGE_KEY);
    sessionStorage.removeItem(AUTH_EMAIL_STORAGE_KEY);
    this.loggedIn.set(false);
    this.role.set(null);
    this.loginEmail.set('');
    this.loginPassword.set('');

    // Always land back on the app root after logout, regardless of which path the user was on (e.g.
    // /backend-system, /launchproviderinapp?iss=...&launch=...). A full-page navigation to '/' resolves to the
    // current origin's root (http://localhost:5501/ in dev, the hosting origin in prod), drops any launch query
    // params, and reboots the app clean on the login screen. Kept relative rather than hardcoding the dev URL so
    // it stays correct wherever the app is served.
    if (window.location.pathname !== '/' || window.location.search) {
      window.location.href = '/';
    }
  }
}
