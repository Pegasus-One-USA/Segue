// auth/pages/mfa-setup-required/mfa-setup-required.component.ts
import { Component, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { AuthService } from '../../services/auth.service';
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
  private readonly router = inject(Router);

  protected readonly finishing = signal(false);

  /** For anyone unable or unwilling to set up MFA right now — signs out rather than leaving them
   *  stuck on this page with no way forward. */
  protected backToSignIn(): void {
    this.auth.logout();
  }

  // The user's current access-token cookie still carries mfa_setup_required: true (it was minted
  // before enrollment finished) — mfaSetupGuard would bounce them right back here if we navigated
  // on the stale claim. Refresh (rotates the cookie via the refresh-token cookie automatically) to
  // mint one with the now-current claim, resync the store from the returned profile, then it's
  // safe to leave.
  protected onEnrolled(): void {
    this.finishing.set(true);

    this.auth.refreshToken().subscribe({
      next: () => this.router.navigate(['/dashboard']),
      error: () => this.router.navigate(['/dashboard']),
    });
  }
}
