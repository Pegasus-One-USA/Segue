// auth/pages/mfa-setup-required/mfa-setup-required.component.ts
import { Component, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { AuthService } from '../../services/auth.service';
import { TokenService } from '../../services/token.service';
import { MfaEnrollmentPanelComponent } from '../../components/mfa-enrollment-panel/mfa-enrollment-panel.component';

@Component({
  selector: 'app-mfa-setup-required',
  standalone: true,
  imports: [MatIconModule, MatProgressSpinnerModule, MfaEnrollmentPanelComponent],
  templateUrl: './mfa-setup-required.component.html',
  styleUrl: './mfa-setup-required.component.scss',
})
export class MfaSetupRequiredComponent {
  private readonly auth   = inject(AuthService);
  private readonly tokens = inject(TokenService);
  private readonly router = inject(Router);

  protected readonly finishing = signal(false);

  /** For anyone unable or unwilling to set up MFA right now — signs out rather than leaving them
   *  stuck on this page with no way forward. */
  protected backToSignIn(): void {
    this.auth.logout();
  }

  // The user's current access token still carries mfa_setup_required: true (it was minted before
  // enrollment finished) — mfaSetupGuard would bounce them right back here if we navigated on the
  // stale token. Refresh to mint a token with the now-current claim, resync the store from it
  // (mirrors AuthService.initFromToken), then it's safe to leave.
  protected onEnrolled(): void {
    this.finishing.set(true);
    const refreshToken = this.tokens.getRefreshToken();
    if (!refreshToken) {
      this.router.navigate(['/dashboard']);
      return;
    }

    this.auth.refreshToken(refreshToken).subscribe({
      next: () => this.auth.initFromToken().subscribe(() => this.router.navigate(['/dashboard'])),
      error: () => this.router.navigate(['/dashboard']),
    });
  }
}
