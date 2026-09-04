import { Component, inject, signal } from '@angular/core';
import { UserProfileService }   from '../../services/user-profile.service';
import { MfaApiService }        from '../../../auth/services/mfa-api.service';
import { MfaStatusResponse }    from '../../../auth/models/mfa.model';
import { extractApiErrorMessage } from '../../../core/http-error.util';
import { MfaEnrollmentPanelComponent } from '../../../auth/components/mfa-enrollment-panel/mfa-enrollment-panel.component';

@Component({
  selector:    'app-security',
  standalone:  true,
  imports:     [MfaEnrollmentPanelComponent],
  templateUrl: './security.component.html',
  styleUrl:    './security.component.scss',
})
export class SecurityComponent {
  private readonly mfaApi = inject(MfaApiService);

  protected readonly profSvc = inject(UserProfileService);

  protected readonly pwChangeOpen = signal(false);

  protected readonly profile   = this.profSvc.profile;
  protected readonly apiKeys   = this.profSvc.apiKeys;

  // ─── MFA (real state via MfaApiService — not the mock profile flag) ────────
  // Enrollment itself is owned by MfaEnrollmentPanelComponent; this page only tracks whether the
  // user is currently enrolled (to decide whether to show the panel or the Disable flow).
  protected readonly mfaStatus   = signal<MfaStatusResponse | null>(null);
  protected readonly mfaBusy     = signal(false);
  protected readonly mfaError    = signal<string | null>(null);
  protected readonly disableOpen = signal(false);

  constructor() {
    this.refreshMfaStatus();
  }

  togglePwChange(): void { this.pwChangeOpen.update(v => !v); }

  revokeApiKey(id: string): void  { this.profSvc.revokeApiKey(id); }

  private refreshMfaStatus(): void {
    this.mfaApi.getStatus().subscribe({
      next: (status) => this.mfaStatus.set(status),
      error: () => this.mfaStatus.set(null),
    });
  }

  onEnrolled(): void {
    this.refreshMfaStatus();
  }

  // ─── Disable flow ───────────────────────────────────────────────────────────
  openDisable(): void {
    this.disableOpen.set(true);
    this.mfaError.set(null);
  }

  cancelDisable(): void {
    this.disableOpen.set(false);
    this.mfaError.set(null);
  }

  confirmDisable(code: string): void {
    if (!code.trim()) return;
    this.mfaBusy.set(true);
    this.mfaError.set(null);
    this.mfaApi.disable(code.trim()).subscribe({
      next: () => {
        this.mfaBusy.set(false);
        this.disableOpen.set(false);
        this.refreshMfaStatus();
      },
      error: (err) => {
        this.mfaBusy.set(false);
        this.mfaError.set(extractApiErrorMessage(err, 'Invalid code. Please try again.'));
      },
    });
  }
}
