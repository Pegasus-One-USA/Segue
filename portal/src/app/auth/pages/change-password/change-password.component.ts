// auth/pages/change-password/change-password.component.ts
import { Component, inject, signal } from '@angular/core';
import {
  ReactiveFormsModule,
  FormBuilder,
  Validators,
  AbstractControl,
  ValidationErrors,
  ValidatorFn,
} from '@angular/forms';
import { RouterLink } from '@angular/router';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatIconModule } from '@angular/material/icon';
import { AuthService } from '../../services/auth.service';
import { PasswordStrengthComponent } from '../../components/password-strength/password-strength.component';
import { ToastService } from '../../../services/toast.service';

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

/** newPassword must differ from currentPassword */
function passwordNotSameValidator(currentKey: string, newKey: string): ValidatorFn {
  return (group: AbstractControl): ValidationErrors | null => {
    const current = group.get(currentKey)?.value ?? '';
    const next    = group.get(newKey)?.value ?? '';
    if (next && current && current === next) {
      group.get(newKey)?.setErrors({ sameAsCurrent: true });
      return { sameAsCurrent: true };
    }
    const newCtrl = group.get(newKey);
    if (newCtrl?.hasError('sameAsCurrent')) {
      const { sameAsCurrent: _, ...rest } = newCtrl.errors ?? {};
      newCtrl.setErrors(Object.keys(rest).length ? rest : null);
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
  selector: 'app-change-password',
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
  templateUrl: './change-password.component.html',
  styleUrl: './change-password.component.scss',
})
export class ChangePasswordComponent {
  private readonly fb       = inject(FormBuilder);
  private readonly auth     = inject(AuthService);
  private readonly toast    = inject(ToastService);

  protected readonly loading          = signal(false);
  protected readonly error            = signal<string | null>(null);
  protected readonly submitted        = signal(false);
  protected readonly showCurrent      = signal(false);
  protected readonly showNew          = signal(false);
  protected readonly showConfirm      = signal(false);

  protected readonly form = this.fb.nonNullable.group(
    {
      currentPassword: ['', Validators.required],
      newPassword:     ['', [Validators.required, passwordStrengthValidator]],
      confirmPassword: ['', Validators.required],
    },
    {
      validators: [
        passwordMatchValidator('newPassword', 'confirmPassword'),
        passwordNotSameValidator('currentPassword', 'newPassword'),
      ],
    }
  );

  get newPasswordValue(): string {
    return this.form.get('newPassword')?.value ?? '';
  }

  protected toggleCurrent(): void { this.showCurrent.update(v => !v); }
  protected toggleNew(): void     { this.showNew.update(v => !v); }
  protected toggleConfirm(): void { this.showConfirm.update(v => !v); }

  protected fieldError(field: string, errorKey: string): boolean {
    const ctrl = this.form.get(field);
    return !!(ctrl?.hasError(errorKey) && (ctrl.touched || this.submitted()));
  }

  protected submit(): void {
    this.submitted.set(true);
    this.form.markAllAsTouched();
    if (this.form.invalid) return;

    const { currentPassword, newPassword, confirmPassword } = this.form.getRawValue();
    this.loading.set(true);
    this.error.set(null);

    this.auth.changePassword({ currentPassword, newPassword, confirmPassword }).subscribe({
      next: () => {
        this.loading.set(false);
        this.form.reset();
        this.submitted.set(false);
        this.toast.success('Password changed successfully!');
      },
      error: (e) => {
        this.loading.set(false);
        this.error.set(e?.error?.message ?? 'Failed to change password. Please check your current password.');
      },
    });
  }
}
