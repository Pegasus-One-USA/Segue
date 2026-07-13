import { Component, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { HttpClient } from '@angular/common/http';
import { ActivatedRoute } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { PatientStandaloneComponent } from './demo-types/demo-type-1/patient-standalone';
import { LaunchProviderInAppComponent } from './demo-types/demo-type-2/launch-provider-in-app';

const BACKEND_BASE_URL = 'http://localhost:5500';
const DEMO_TYPE_STORAGE_KEY = 'hb_demo_type';

interface DemoType {
  id: number;
  name: string;
}

interface LoginResponse {
  email: string;
  role: 'Admin' | 'Patient';
}

@Component({
  selector: 'app-root',
  imports: [FormsModule, PatientStandaloneComponent, LaunchProviderInAppComponent],
  templateUrl: './app.html',
  styleUrl: './app.scss'
})
export class App implements OnInit {
  protected readonly loggedIn = signal(false);
  protected readonly role = signal<'Admin' | 'Patient' | null>(null);
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

  constructor(private readonly http: HttpClient, private readonly route: ActivatedRoute) {}

  ngOnInit(): void {
    // The OAuth "Connect Get Data" flow opens the workflow URL in a new tab; when it redirects back the
    // Angular app boots from scratch in that tab. Re-check the hb_session cookie (shared across tabs, unlike
    // this component's signals) so an already-logged-in user lands on the dashboard instead of the login screen.
    void this.restoreSession();
    void this.loadDemoTypes();
  }

  private async restoreSession(): Promise<void> {
    try {
      const response = await firstValueFrom(
        this.http.get<LoginResponse>(`${BACKEND_BASE_URL}/api/session`, { withCredentials: true })
      );
      this.role.set(response.role);
      this.loggedIn.set(true);
    } catch {
      // No valid session cookie — stay on the login screen.
    }
  }

  private async loadDemoTypes(): Promise<void> {
    try {
      const demoTypes = await firstValueFrom(this.http.get<DemoType[]>(`${BACKEND_BASE_URL}/api/demo-types`));
      this.demoTypes.set(demoTypes);

      const requestedParam = this.route.snapshot.queryParamMap.get('DemoType');
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
    } catch {
      this.loginError.set('Invalid email or password.');
    }
  }

  async logout(): Promise<void> {
    await firstValueFrom(this.http.post(`${BACKEND_BASE_URL}/api/logout`, {}, { withCredentials: true }));
    this.loggedIn.set(false);
    this.role.set(null);
    this.loginEmail.set('');
    this.loginPassword.set('');
  }
}
