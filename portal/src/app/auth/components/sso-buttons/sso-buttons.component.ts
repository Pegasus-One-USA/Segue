/**
 * SsoButtonsComponent — the shared "or continue with" SSO section used by the login,
 * setup-superadmin, and accept-invite pages.
 *
 * It renders "Continue with Microsoft" / "Continue with Google" ONLY for providers that
 * SsoConfigService reports as enabled, and nothing at all when neither is enabled (graceful
 * degradation). On click it invokes SsoService to obtain `{ provider, token }` and emits it via
 * `authenticated`; the host page then calls the appropriate backend endpoint. Errors surface via
 * `failed` and are also shown inline. A `verb` input customises the label ("Sign in" vs "Create"
 * vs "Continue").
 */
import { Component, inject, input, output, signal } from '@angular/core';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { SsoConfigService } from '../../services/sso-config.service';
import { SsoService, SsoResult, SsoProvider } from '../../services/sso.service';
import { AUTH_ENDPOINTS } from '../../../core/api-endpoints';

@Component({
  selector: 'app-sso-buttons',
  standalone: true,
  imports: [MatProgressSpinnerModule],
  templateUrl: './sso-buttons.component.html',
  styleUrl: './sso-buttons.component.scss',
})
export class SsoButtonsComponent {
  private readonly ssoConfig = inject(SsoConfigService);
  private readonly sso       = inject(SsoService);

  /** Leading verb for button labels: "Continue" (default), "Sign in", "Create account", … */
  readonly verb = input<string>('Continue');
  /** Disable the buttons while the host is busy (e.g. its own submit in flight). */
  readonly disabled = input<boolean>(false);

  /** Emits the obtained IdP token; the host calls the matching backend endpoint. */
  readonly authenticated = output<SsoResult>();
  /** Emits a user-facing error message when obtaining the IdP token fails. */
  readonly failed = output<string>();

  protected readonly entraEnabled  = this.ssoConfig.entraEnabled;
  protected readonly googleEnabled = this.ssoConfig.googleEnabled;
  protected readonly samlEnabled   = this.ssoConfig.samlEnabled;
  protected readonly anyEnabled    = () => this.ssoConfig.anyEnabled();

  protected readonly busy       = signal<SsoProvider | null>(null);
  protected readonly localError = signal('');

  constructor() {
    // Lazily ensure config is loaded so the buttons can render (idempotent + resilient).
    void this.ssoConfig.load();
  }

  protected async continueWith(provider: SsoProvider): Promise<void> {
    if (this.busy() || this.disabled()) return;
    this.localError.set('');
    this.busy.set(provider);
    try {
      if (provider === 'Entra') {
        // loginRedirect navigates the whole tab away — there's no token to emit here. The
        // response is picked up by SsoService.handleRedirectResponse() on the next app boot,
        // after the browser returns from Microsoft (see app.config.ts).
        await this.sso.signInWithEntra();
        return;
      }

      const result: SsoResult = await this.sso.signInWithGoogle();
      this.authenticated.emit(result);
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Single sign-on failed. Please try again.';
      this.localError.set(message);
      this.failed.emit(message);
    } finally {
      this.busy.set(null);
    }
  }

  /**
   * SAML has no client-side token to obtain — it's a plain full-page redirect to the IdP, which
   * eventually POSTs an assertion back to the API's ACS endpoint and redirects into the portal.
   * Unlike Entra/Google there's no `authenticated`/`failed` round-trip through this component.
   */
  protected continueWithSaml(): void {
    if (this.busy() || this.disabled()) return;
    window.location.href = AUTH_ENDPOINTS.samlLogin;
  }
}
