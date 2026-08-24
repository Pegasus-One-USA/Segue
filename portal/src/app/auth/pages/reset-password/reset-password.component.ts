// auth/pages/reset-password/reset-password.component.ts
import { Component, inject, signal } from '@angular/core';
import {
  ReactiveFormsModule,
  FormBuilder,
  Validators,
  AbstractControl,
  ValidationErrors,
  ValidatorFn,
} from '@angular/forms';
import { RouterLink, ActivatedRoute, Router } from '@angular/router';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatIconModule } from '@angular/material/icon';
import { AuthService } from '../../services/auth.service';
import { PasswordStrengthComponent } from '../../components/password-strength/password-strength.component';

// ── Validators ─────────────────────────────────────────────────────────────────

function passwordMatchValidator(pwKey: string, confirmKey: string): ValidatorFn {
  return (group: AbstractControl): ValidationErrors | null => {
    const pw  = group.get(pwKey)?.value ?? '';
    const cpw = group.get(confirmKey)?.value ?? '';
    if (cpw && pw !== cpw) {
      group.get(confirmKey)?.setErrors({ passwordMismatch: true });
      return { passwordMismatch: true };
    }
    const confirmCtrl = group.get(confirmKey);
    if (confirmCtrl?.hasError('passwordMismatch')) {
      const { passwordMismatch: _, ...rest } = confirmCtrl.errors ?? {};
      confirmCtrl.setErrors(Object.keys(rest).length ? rest : null);
    }
    return null;
  };
}

// Must match LocalAuthService.ValidatePassword (backend) exactly — 12 chars minimum, upper+lower+digit+
// special. This used to be looser (8 chars, no lowercase requirement) than what the server actually
// enforces, so a password the UI accepted as "strong enough" could still 400 at the server.
function passwordStrengthValidator(ctrl: AbstractControl): ValidationErrors | null {
  const p = ctrl.value as string;
  if (!p) return null;
  const missing: string[] = [];
  if (p.length < 12)            missing.push('minLength');
  if (!/[A-Z]/.test(p))         missing.push('uppercase');
  if (!/[a-z]/.test(p))         missing.push('lowercase');
  if (!/[0-9]/.test(p))         missing.push('number');
  if (!/[^A-Za-z0-9]/.test(p)) missing.push('specialChar');
  return missing.length ? { passwordStrength: { missing } } : null;
}

// ── Component ──────────────────────────────────────────────────────────────────

@Component({
  selector: 'app-reset-password',
  standalone: true,
  imports: [
    ReactiveFormsModule,
    RouterLink,
    MatFormFieldModule,
    MatInputModule,
    MatButtonModule,
    MatProgressSpinnerModule,
    MatIconModule,
    PasswordStrengthComponent,
  ],
  templateUrl: './reset-password.component.html',
  styleUrl: './reset-password.component.scss',
})
export class ResetPasswordComponent {
  private readonly fb     = inject(FormBuilder);
  private readonly auth   = inject(AuthService);
  private readonly route  = inject(ActivatedRoute);
  private readonly router = inject(Router);

  private readonly token = this.route.snapshot.queryParamMap.get('token') ?? '';
  private readonly email = this.route.snapshot.queryParamMap.get('email') ?? '';

  protected readonly done         = signal(false);
  protected readonly loading      = signal(false);
  protected readonly error        = signal<string | null>(null);
  protected readonly submitted    = signal(false);
  protected readonly showPassword = signal(false);
  protected readonly showConfirm  = signal(false);
  /** Set when the backend rejects the token itself (401 — invalid, tampered, already used, or expired).
   *  The backend deliberately returns the same generic error for all of those cases (distinguishing them
   *  would leak whether a token/account ever existed), so the UI shows one combined "request a new link"
   *  state rather than a wrong/misleading "expired" vs. "invalid" guess. A weak-password (400) or network
   *  error does NOT set this — those keep the form visible so the user can just fix the password and retry
   *  without having to request a whole new email. */
  protected readonly linkRejected = signal(false);

  protected readonly hasToken = !!this.token && !!this.email;

  protected readonly form = this.fb.nonNullable.group(
    {
      newPassword:     ['', [Validators.required, passwordStrengthValidator]],
      confirmPassword: ['', Validators.required],
    },
    { validators: passwordMatchValidator('newPassword', 'confirmPassword') }
  );

  get passwordValue(): string {
    return this.form.get('newPassword')?.value ?? '';
  }

  protected togglePassword(): void { this.showPassword.update(v => !v); }
  protected toggleConfirm(): void  { this.showConfirm.update(v => !v); }

  protected fieldError(field: string, errorKey: string): boolean {
    const ctrl = this.form.get(field);
    return !!(ctrl?.hasError(errorKey) && (ctrl.touched || this.submitted()));
  }

  protected submit(): void {
    this.submitted.set(true);
    this.form.markAllAsTouched();

    if (!this.token || !this.email) {
      this.error.set('Invalid or expired reset link. Please request a new one.');
      return;
    }
    if (this.form.invalid) return;

    const { newPassword, confirmPassword } = this.form.getRawValue();
    this.loading.set(true);
    this.error.set(null);
    this.linkRejected.set(false);

    this.auth.resetPassword({ token: this.token, email: this.email, newPassword, confirmPassword }).subscribe({
      next: () => {
        this.loading.set(false);
        this.done.set(true);
        // "Redirect to Login after successful reset" — the user must sign in again (no auto-login, see
        // AuthService/LocalAuthService.ResetPasswordAsync, which never issues a session). Auto-navigates
        // after a short pause so the success message is still readable; the visible "Sign In" button
        // covers anyone who doesn't want to wait.
        setTimeout(() => this.router.navigate(['/auth/login']), 3000);
      },
      error: (e) => {
        this.loading.set(false);
        // The backend returns 401 for every token problem (invalid, tampered, reused, or expired) by
        // design — see LocalAuthService.ResetPasswordAsync — so this can't and shouldn't try to guess
        // which one occurred. A 400 here is a rejected password (policy mismatch the client-side
        // validator didn't already catch); anything else is a network/server error.
        if (e?.status === 401) {
          this.linkRejected.set(true);
        } else if (e?.status === 0) {
          this.error.set('Network error. Please check your connection and try again.');
        } else {
          this.error.set(e?.error?.message ?? 'Reset failed. Please try again.');
        }
      },
    });
  }
}
