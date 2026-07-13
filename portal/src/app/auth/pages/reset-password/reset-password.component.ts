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
import { RouterLink, ActivatedRoute } from '@angular/router';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatIconModule } from '@angular/material/icon';
import { AuthService } from '../../services/auth.service';
import { PasswordStrengthComponent } from '../../components/password-strength/password-strength.component';
import { authErrorMessage } from '../../services/http-error.util';

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

function passwordStrengthValidator(ctrl: AbstractControl): ValidationErrors | null {
  const p = ctrl.value as string;
  if (!p) return null;
  const missing: string[] = [];
  if (p.length < 8)             missing.push('minLength');
  if (!/[A-Z]/.test(p))         missing.push('uppercase');
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
  private readonly fb    = inject(FormBuilder);
  private readonly auth  = inject(AuthService);
  private readonly route = inject(ActivatedRoute);

  private readonly token = this.route.snapshot.queryParamMap.get('token') ?? '';

  protected readonly done         = signal(false);
  protected readonly loading      = signal(false);
  protected readonly error        = signal<string | null>(null);
  protected readonly submitted    = signal(false);
  protected readonly showPassword = signal(false);
  protected readonly showConfirm  = signal(false);

  protected readonly hasToken = !!this.token;

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

    if (!this.token) {
      this.error.set('Invalid or expired reset link. Please request a new one.');
      return;
    }
    if (this.form.invalid) return;

    const { newPassword, confirmPassword } = this.form.getRawValue();
    this.loading.set(true);
    this.error.set(null);

    this.auth.resetPassword({ token: this.token, newPassword, confirmPassword }).subscribe({
      next: () => {
        this.loading.set(false);
        this.done.set(true);
      },
      error: (e) => {
        this.loading.set(false);
        this.error.set(authErrorMessage(e, 'Reset failed. The link may have expired.'));
      },
    });
  }
}
