import { Component, inject, signal, computed } from '@angular/core';
import { Router } from '@angular/router';
import { toSignal } from '@angular/core/rxjs-interop';
import {
  ReactiveFormsModule, FormBuilder, Validators,
  AbstractControl, ValidationErrors,
} from '@angular/forms';
import { startWith } from 'rxjs/operators';
import { HttpErrorResponse } from '@angular/common/http';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';

import { AppInitService } from '../../services/app-init.service';
import { PasswordPolicyService } from '../../../auth/services/password-policy.service';
import { PasswordValidation } from '../../../auth/models/password-policy.model';
import { AuthStore } from '../../../auth/store/auth.store';
import { SessionService } from '../../../auth/services/session.service';
import { TokenService } from '../../../auth/services/token.service';
import { buildUserFromJwt } from '../../../auth/services/jwt-user.mapper';

function matchPasswords(group: AbstractControl): ValidationErrors | null {
  const pw  = group.get('password')?.value        as string;
  const cfm = group.get('confirmPassword')?.value as string;
  return pw && cfm && pw !== cfm ? { mismatch: true } : null;
}

@Component({
  selector: 'app-setup-super-admin',
  standalone: true,
  imports: [
    ReactiveFormsModule,
    MatButtonModule,
    MatIconModule,
    MatProgressSpinnerModule,
    MatSnackBarModule,
  ],
  templateUrl: './setup-super-admin.component.html',
  styleUrl: './setup-super-admin.component.scss',
})
export class SetupSuperAdminComponent {
  private readonly router    = inject(Router);
  private readonly fb        = inject(FormBuilder);
  private readonly appInit   = inject(AppInitService);
  private readonly policySvc = inject(PasswordPolicyService);
  private readonly snackBar  = inject(MatSnackBar);

  // Login success handling — reuse the exact login pattern (see AuthService.login).
  private readonly store    = inject(AuthStore);
  private readonly session  = inject(SessionService);
  private readonly tokens   = inject(TokenService);

  protected readonly isLoading   = signal(false);
  protected readonly submitted   = signal(false);
  protected readonly showPw      = signal(false);
  protected readonly showCfm     = signal(false);
  protected readonly serverError = signal('');

  protected readonly form = this.fb.nonNullable.group({
    firstName:       ['', [Validators.required, Validators.maxLength(80)]],
    lastName:        ['', [Validators.required, Validators.maxLength(80)]],
    email:           ['', [Validators.required, Validators.email]],
    password:        ['', [Validators.required, Validators.minLength(12), Validators.maxLength(64)]],
    confirmPassword: ['', Validators.required],
  }, { validators: matchPasswords });

  // Signal-backed live values for reactive computed
  protected readonly pwValue = toSignal(
    this.form.get('password')!.valueChanges.pipe(startWith('')),
    { initialValue: '' }
  );
  private readonly cfmValue = toSignal(
    this.form.get('confirmPassword')!.valueChanges.pipe(startWith('')),
    { initialValue: '' }
  );

  protected readonly pwValidation = computed<PasswordValidation>(() =>
    this.policySvc.validate(this.pwValue())
  );

  protected readonly passwordsMatch = computed(() =>
    this.pwValue() !== '' && this.pwValue() === this.cfmValue()
  );

  protected readonly canSubmit = computed(() =>
    this.form.get('firstName')!.valid &&
    this.form.get('lastName')!.valid &&
    this.form.get('email')!.valid &&
    this.pwValidation().allMet &&
    this.passwordsMatch()
  );

  protected togglePw():  void { this.showPw.update(v => !v); }
  protected toggleCfm(): void { this.showCfm.update(v => !v); }

  protected submit(): void {
    this.submitted.set(true);
    this.serverError.set('');

    if (!this.canSubmit()) {
      this.form.markAllAsTouched();
      return;
    }

    const { firstName, lastName, email, password } = this.form.getRawValue();
    const displayName = `${firstName} ${lastName}`.trim();

    this.isLoading.set(true);

    this.appInit.createSuperAdmin({ email, displayName, password }).subscribe({
      next: (res) => {
        // Reuse the exact login success handling: store tokens + rebuild user from the JWT.
        const payload = this.tokens.decodePayload<Record<string, unknown>>(res.accessToken) ?? {};
        const user = buildUserFromJwt(payload);
        user.mustChangePassword = res.requiresPasswordChange ?? false;

        this.store.setUser(user);
        this.session.start(user.id, res.accessToken, res.refreshToken ?? '', false);
        this.isLoading.set(false);
        this.router.navigate(['/dashboard']);
      },
      error: (err: HttpErrorResponse) => {
        this.isLoading.set(false);
        if (err?.status === 409) {
          const msg = 'Setup already completed — please go to the sign-in page.';
          this.serverError.set(msg);
          this.snackBar.open(msg, 'Go to Sign In', { duration: 8000, panelClass: ['snack-error'] })
            .onAction().subscribe(() => this.router.navigate(['/auth/login']));
          return;
        }
        const message = err?.error?.message ?? 'Setup failed. Please try again.';
        this.serverError.set(message);
        this.snackBar.open(message, 'Dismiss', { duration: 6000, panelClass: ['snack-error'] });
      },
    });
  }

  protected strengthLabel(v: PasswordValidation): string {
    return { weak: 'Weak', fair: 'Fair', good: 'Good', strong: 'Strong' }[v.strength];
  }

  protected strengthWidth(v: PasswordValidation): string {
    return `${v.score}%`;
  }
}
