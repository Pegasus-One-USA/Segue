// auth/pages/signup/signup.component.ts
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
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatIconModule } from '@angular/material/icon';
import { MatDialog } from '@angular/material/dialog';
import { AuthService } from '../../services/auth.service';
import { PasswordStrengthComponent } from '../../components/password-strength/password-strength.component';
import { UserRole } from '../../models/user.model';
import {
  TermsAndConditionsDialogComponent,
  TermsAndConditionsDialogResult,
} from '../../../onboarding/components/terms-and-conditions-dialog/terms-and-conditions-dialog.component';

// ── Validators ─────────────────────────────────────────────────────────────────

/** Cross-field: newPassword === confirmPassword */
function passwordMatchValidator(passwordKey: string, confirmKey: string): ValidatorFn {
  return (group: AbstractControl): ValidationErrors | null => {
    const pw  = group.get(passwordKey)?.value ?? '';
    const cpw = group.get(confirmKey)?.value ?? '';
    if (cpw && pw !== cpw) {
      group.get(confirmKey)?.setErrors({ passwordMismatch: true });
      return { passwordMismatch: true };
    }
    // Clear the mismatch error if they now match (preserve others)
    const confirmCtrl = group.get(confirmKey);
    if (confirmCtrl?.hasError('passwordMismatch')) {
      const { passwordMismatch: _, ...rest } = confirmCtrl.errors ?? {};
      confirmCtrl.setErrors(Object.keys(rest).length ? rest : null);
    }
    return null;
  };
}

/** Single-field: password must meet complexity rules */
function passwordStrengthValidator(ctrl: AbstractControl): ValidationErrors | null {
  const p = ctrl.value as string;
  if (!p) return null;
  const missing: string[] = [];
  if (p.length < 8)           missing.push('minLength');
  if (!/[A-Z]/.test(p))       missing.push('uppercase');
  if (!/[0-9]/.test(p))       missing.push('number');
  if (!/[^A-Za-z0-9]/.test(p)) missing.push('specialChar');
  return missing.length ? { passwordStrength: { missing } } : null;
}

/** Single-field: checkbox must be checked */
function mustBeTrueValidator(ctrl: AbstractControl): ValidationErrors | null {
  return ctrl.value === true ? null : { mustBeTrue: true };
}

// ── Component ──────────────────────────────────────────────────────────────────

interface RoleOption { value: UserRole; label: string; }

@Component({
  selector: 'app-signup',
  standalone: true,
  imports: [
    ReactiveFormsModule,
    RouterLink,
    MatFormFieldModule,
    MatInputModule,
    MatButtonModule,
    MatCheckboxModule,
    MatProgressSpinnerModule,
    MatIconModule,
    PasswordStrengthComponent,
  ],
  templateUrl: './signup.component.html',
  styleUrl: './signup.component.scss',
})
export class SignupComponent {
  private readonly fb     = inject(FormBuilder);
  private readonly dialog = inject(MatDialog);
  protected readonly auth = inject(AuthService);

  protected readonly isLoading = this.auth.isLoading;
  protected readonly error     = this.auth.error;

  protected readonly showPassword = signal(false);
  protected readonly showConfirm  = signal(false);
  protected readonly submitted    = signal(false);

  protected readonly roles: RoleOption[] = [
    { value: 'Audit',      label: 'Audit'      },
    { value: 'Operations', label: 'Operations' },
    { value: 'Admin',      label: 'Admin'      },
  ];

  protected readonly form = this.fb.nonNullable.group(
    {
      firstName:       ['', [Validators.required, Validators.minLength(2)]],
      lastName:        ['', [Validators.required, Validators.minLength(2)]],
      email:           ['', [Validators.required, Validators.email]],
      password:        ['', [Validators.required, passwordStrengthValidator]],
      confirmPassword: ['', Validators.required],
      role:            ['Audit' as UserRole],
      // Starts disabled — enabled only once the Terms and Conditions dialog has been opened and
      // closed (see openTermsDialog()).
      acceptTerms:     [{ value: false, disabled: true }, mustBeTrueValidator],
    },
    { validators: passwordMatchValidator('password', 'confirmPassword') }
  );

  get passwordValue(): string {
    return this.form.get('password')?.value ?? '';
  }

  protected togglePassword(): void { this.showPassword.update(v => !v); }
  protected toggleConfirm(): void  { this.showConfirm.update(v => !v); }

  protected openTermsDialog(): void {
    this.dialog
      .open<TermsAndConditionsDialogComponent, void, TermsAndConditionsDialogResult>(
        TermsAndConditionsDialogComponent, { autoFocus: false, restoreFocus: true })
      .afterClosed()
      .subscribe((result) => {
        if (result?.readToEnd) {
          this.form.get('acceptTerms')!.enable();
        }
      });
  }

  protected fieldError(field: string, error: string): boolean {
    const ctrl = this.form.get(field);
    return !!(ctrl?.hasError(error) && (ctrl.touched || this.submitted()));
  }

  protected submit(): void {
    this.submitted.set(true);
    this.form.markAllAsTouched();
    // A disabled control is excluded from its parent FormGroup's aggregate validity in Angular
    // Reactive Forms, so form.invalid alone would NOT catch "acceptTerms still disabled/unchecked" —
    // check it explicitly.
    if (this.form.invalid || this.form.get('acceptTerms')!.value !== true) return;

    const { firstName, lastName, email, password, role, acceptTerms } = this.form.getRawValue();
    this.auth.register({
      firstName: firstName.trim(),
      lastName:  lastName.trim(),
      email:     email.trim(),
      password,
      role:      role as UserRole,
      acceptTerms,
    }).subscribe();
  }
}
