import { Component, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { PatientStandaloneComponent } from './demo-types/demo-type-1/patient-standalone';
import { LaunchProviderInAppComponent } from './demo-types/demo-type-2/launch-provider-in-app';
import { LaunchStandaloneProviderComponent } from './demo-types/provider-standalone/launch-standalone-provider';
import { LaunchStandalonePatientComponent } from './demo-types/patient-standalone/launch-standalone-patient';

const BACKEND_BASE_URL = 'http://localhost:5500';
const DEMO_TYPE_STORAGE_KEY = 'hb_demo_type';

// Embedded EHR launches round-trip this tab through FHIRBridge + Epic and back to this same origin via a full
// top-level navigation *inside the iframe* Epic embeds this app in (see LaunchProviderInAppComponent.ngOnInit).
// hb_session is SameSite=Lax with no Secure flag, so on that return leg the browser won't send it back — Lax
// cookies are excluded from requests made in a cross-site iframe context, even though the navigation looks
// top-level from inside the frame. That made restoreSession()'s cookie check fail after every embedded launch,
// re-showing the login screen and forcing a second login for a session that was already established. sessionStorage
// isn't subject to that restriction (it's plain per-origin storage, not a cookie sent on requests), so mirror the
// logged-in role there at login and trust it first on restore — sidesteps the blocked cookie read entirely.
const AUTH_ROLE_STORAGE_KEY = 'hb_auth_role';

// A direct EHR-launch redirect (Epic calling this app's registered launch URL) lands on this exact path
// with ?iss=&launch= already on the URL — lock the Login Type from the path itself, no dropdown/?DemoType=
// param needed. Not a seeded DemoType row: it's purely a display label for this entry point, reusing
// LaunchProviderInAppComponent (the "DemoType2" component) since the EHR-launch exchange logic is identical.
const PROVIDER_STANDALONE_PATH = '/launchproviderinapp';
const PROVIDER_STANDALONE_NAME = 'Provider_InApp';

// True SMART Standalone Launch (provider-initiated, not EHR-initiated): a real "Provider_Standalone" DemoType
// row selected from the dropdown. Unlike PROVIDER_STANDALONE_PATH above, there's no incoming iss/launch to
// detect — the redirect below is purely "send this Login Type to its own screen after login."
const STANDALONE_PROVIDER_PATH = '/launchinstandaloneprovider';
const STANDALONE_PROVIDER_NAME = 'Provider_Standalone';

// Patient_Standalone gets its own dedicated path too, same reasoning as the two above — every Login Type
// lands on its own URL after login rather than sharing whatever path the login screen itself was served from.
const PATIENT_STANDALONE_PATH = '/launchpatientstandalone';
const PATIENT_STANDALONE_NAME = 'Patient_Standalone';

interface DemoType {
  id: number;
  name: string;
}

// The backend seeds four roles (Admin, Patient, ProviderStandalone, ProviderInApp — see HealthAppDbContext.cs) —
// this app only ever does an `=== 'Admin'`-style string comparison against it (see PatientStandaloneComponent),
// so there's no real union to enumerate here; typing it narrowly before caused ProviderStandalone/ProviderInApp
// logins to fail the restoreSession() sessionStorage check below.
interface LoginResponse {
  email: string;
  role: string;
}

@Component({
  selector: 'app-root',
  imports: [FormsModule, PatientStandaloneComponent, LaunchProviderInAppComponent, LaunchStandaloneProviderComponent, LaunchStandalonePatientComponent],
  templateUrl: './app.html',
  styleUrl: './app.scss'
})
export class App implements OnInit {
  protected readonly loggedIn = signal(false);
  protected readonly role = signal<string | null>(null);
  protected readonly loginEmail = signal('');
  protected readonly loginPassword = signal('');
  protected readonly loginError = signal('');

  // Login Type (drives which DemoType component loads post-login); persisted in sessionStorage so it
  // survives a page reload within the same browser tab/session.
  protected readonly demoTypes = signal<DemoType[]>([]);
  protected readonly selectedDemoType = signal(sessionStorage.getItem(DEMO_TYPE_STORAGE_KEY) ?? '');

  // Set when a `?DemoType=<id>` query param on the login URL matches a real DemoType row — hides the
  // dropdown and shows the resolved name instead, so a link can pre-select the Login Type for the user.
  protected readonly lockedDemoTypeName = signal<string | null>(null);

  constructor(private readonly http: HttpClient) {}

  ngOnInit(): void {
    // The OAuth "Connect Get Data" flow opens the workflow URL in a new tab; when it redirects back the
    // Angular app boots from scratch in that tab. Re-check the hb_session cookie (shared across tabs, unlike
    // this component's signals) so an already-logged-in user lands on the dashboard instead of the login screen.
    void this.restoreSession();
    void this.loadDemoTypes();
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
    } catch {
      // No valid session cookie — stay on the login screen.
    }
  }

  private async loadDemoTypes(): Promise<void> {
    // Read query params straight off window.location.search rather than ActivatedRoute: this app has no
    // <router-outlet> (see app.routes.ts), so ActivatedRoute.snapshot isn't reliably populated by the time this
    // root component's ngOnInit runs.
    const params = new URLSearchParams(window.location.search);

    // Locks onto Provider_InApp from either signal: the app already sitting on /launchproviderinapp, or an
    // incoming EHR launch's ?iss=&launch= params landing on some other path (e.g. Epic's app registration
    // still points at the bare root) — either way this is a provider in-app launch, not a dropdown pick.
    const hasLaunchParams = !!params.get('iss') && !!params.get('launch');
    if (window.location.pathname.toLowerCase() === PROVIDER_STANDALONE_PATH || hasLaunchParams) {
      this.lockedDemoTypeName.set(PROVIDER_STANDALONE_NAME);
      this.onDemoTypeChange(PROVIDER_STANDALONE_NAME);
    }

    try {
      const demoTypes = await firstValueFrom(this.http.get<DemoType[]>(`${BACKEND_BASE_URL}/api/demo-types`));
      this.demoTypes.set(demoTypes);

      if (this.lockedDemoTypeName()) {
        return; // Path-based lock above already decided the Login Type.
      }

      const requestedParam = params.get('DemoType');
      const requestedId = requestedParam !== null ? Number(requestedParam) : null;
      const matched = requestedId !== null && Number.isFinite(requestedId)
        ? demoTypes.find((type) => type.id === requestedId)
        : undefined;

      if (matched) {
        this.lockedDemoTypeName.set(matched.name);
        this.onDemoTypeChange(matched.name);
      } else if (!this.selectedDemoType() && demoTypes.length > 0) {
        this.onDemoTypeChange(demoTypes[0].name);
      }
    } catch {
      // Non-fatal — the Login Type dropdown is just empty; it doesn't gate login itself.
    }
  }

  onDemoTypeChange(name: string): void {
    this.selectedDemoType.set(name);
    sessionStorage.setItem(DEMO_TYPE_STORAGE_KEY, name);
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

      // Provider in-app launches must land on /launchproviderinapp so LaunchProviderInAppComponent's own
      // ngOnInit (which reads iss/launch and hands off to FHIRBridge) actually runs — whatever path the
      // login screen itself was served from. Preserves the query string (iss/launch) across the hop.
      if (this.selectedDemoType() === PROVIDER_STANDALONE_NAME
        && window.location.pathname.toLowerCase() !== PROVIDER_STANDALONE_PATH) {
        window.location.href = PROVIDER_STANDALONE_PATH + window.location.search;
      }

      // Same reasoning for the true-standalone flow: land on its own screen so LaunchStandaloneProviderComponent
      // can show the hospital list (or, on the way back from Epic, the Fetch Patient List button).
      if (this.selectedDemoType() === STANDALONE_PROVIDER_NAME
        && window.location.pathname.toLowerCase() !== STANDALONE_PROVIDER_PATH) {
        window.location.href = STANDALONE_PROVIDER_PATH + window.location.search;
      }

      // Patient_Standalone lands on its own path too — never launchproviderinapp or launchinstandaloneprovider.
      if (this.selectedDemoType() === PATIENT_STANDALONE_NAME
        && window.location.pathname.toLowerCase() !== PATIENT_STANDALONE_PATH) {
        window.location.href = PATIENT_STANDALONE_PATH + window.location.search;
      }
    } catch {
      this.loginError.set('Invalid email or password.');
    }
  }

  async logout(): Promise<void> {
    await firstValueFrom(this.http.post(`${BACKEND_BASE_URL}/api/logout`, {}, { withCredentials: true }));
    sessionStorage.removeItem(AUTH_ROLE_STORAGE_KEY);
    this.loggedIn.set(false);
    this.role.set(null);
    this.loginEmail.set('');
    this.loginPassword.set('');
  }
}
