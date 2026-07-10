// auth/pages/login/login.component.ts
import { Component, inject, signal } from '@angular/core';
import { ReactiveFormsModule, FormBuilder, Validators } from '@angular/forms';
import { RouterLink, ActivatedRoute } from '@angular/router';
import { toSignal } from '@angular/core/rxjs-interop';
import { map } from 'rxjs/operators';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar } from '@angular/material/snack-bar';
import { Router } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { AuthService } from '../../services/auth.service';
import { SsoButtonsComponent } from '../../components/sso-buttons/sso-buttons.component';
import { SsoAuthApiService } from '../../services/sso-auth-api.service';
import { SsoResult } from '../../services/sso.service';
import { extractApiErrorMessage } from '../../../core/http-error.util';

type LoginStage = 'credentials' | 'mfa';

@Component({
  selector: 'app-login',
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
    SsoButtonsComponent,
  ],
  templateUrl: './login.component.html',
  styleUrl: './login.component.scss',
})
export class LoginComponent {
  private readonly fb       = inject(FormBuilder);
  private readonly route    = inject(ActivatedRoute);
  private readonly router   = inject(Router);
  private readonly ssoApi   = inject(SsoAuthApiService);
  private readonly snackBar = inject(MatSnackBar);
  protected readonly auth = inject(AuthService);

  protected readonly ssoBusy = signal(false);

  protected readonly isLoading = this.auth.isLoading;
  protected readonly error     = this.auth.error;

  protected readonly activated = toSignal(
    this.route.queryParamMap.pipe(map(p => p.get('activated') === '1')),
    { initialValue: false },
  );

  protected readonly showPassword = signal(false);
  protected readonly capsLockOn   = signal(false);
  protected readonly submitted    = signal(false);

  protected readonly form = this.fb.nonNullable.group({
    email:      ['', [Validators.required, Validators.email]],
    password:   ['', [Validators.required, Validators.minLength(8)]],
    rememberMe: [false],
  });

  // ─── MFA challenge stage ────────────────────────────────────────────────────
  protected readonly stage = signal<LoginStage>('credentials');
  protected readonly mfaSubmitted = signal(false);
  private mfaChallengeToken = '';
  private email = '';
  private rememberMe = false;

  protected readonly mfaForm = this.fb.nonNullable.group({
    code: ['', [Validators.required, Validators.minLength(6)]],
  });

  protected togglePassword(): void {
    this.showPassword.update(v => !v);
  }

  protected onKeydown(e: KeyboardEvent): void {
    this.capsLockOn.set(e.getModifierState('CapsLock'));
  }

  protected submit(): void {
    this.submitted.set(true);
    if (this.form.invalid) return;

    const { email, password, rememberMe } = this.form.getRawValue();
    this.email = email.trim();
    this.rememberMe = rememberMe;
    this.auth.login({ email: this.email, password, rememberMe }).subscribe(res => {
      if (res?.requiresMfa) {
        this.mfaChallengeToken = res.mfaChallengeToken;
        this.mfaSubmitted.set(false);
        this.mfaForm.reset({ code: '' });
        this.stage.set('mfa');
      }
    });
  }

  // ─── MFA code submission ───────────────────────────────────────────────────
  protected submitMfa(): void {
    this.mfaSubmitted.set(true);
    if (this.mfaForm.invalid) return;

    const { code } = this.mfaForm.getRawValue();
    this.auth.completeMfaLogin(this.email, this.mfaChallengeToken, code.trim(), this.rememberMe).subscribe({
      error: (err) => {
        const message = extractApiErrorMessage(err, '');
        if (message.toLowerCase().includes('expired')) {
          // The challenge itself is dead (backend-issued expiry, or unknown token) — there's
          // nothing to retry against, so send the user back to re-enter their password.
          this.stage.set('credentials');
          this.form.patchValue({ password: '' });
        }
      },
    });
  }

  protected backToCredentials(): void {
    this.stage.set('credentials');
    this.mfaForm.reset({ code: '' });
  }

  // Helpers for template validation display. Angular's strictly-typed reactive forms give `form`
  // and `mfaForm` incompatible `.get()` overloads, so a single generic helper can't type-check
  // cleanly across both — two one-line methods are simpler than fighting that.
  protected fieldError(field: string, error: string): boolean {
    const ctrl = this.form.get(field);
    return !!(ctrl?.hasError(error) && (ctrl.touched || this.submitted()));
  }

  protected mfaFieldError(error: string): boolean {
    const ctrl = this.mfaForm.get('code');
    return !!(ctrl?.hasError(error) && (ctrl.touched || this.mfaSubmitted()));
  }

  // ─── SSO ───────────────────────────────────────────────────────────────────
  protected onSsoAuthenticated(result: SsoResult): void {
    this.ssoBusy.set(true);
    this.ssoApi.ssoLogin(result.provider, result.token).subscribe({
      next: () => {
        this.ssoBusy.set(false);
        this.router.navigate(['/dashboard']);
      },
      error: (err: HttpErrorResponse) => {
        this.ssoBusy.set(false);
        const message = err?.status === 401
          ? 'No matching account found for this identity. Ask an administrator for an invitation.'
          : err?.error?.message ?? 'Single sign-on failed. Please try again.';
        this.snackBar.open(message, 'Dismiss', { duration: 6000, panelClass: ['snack-error'] });
      },
    });
  }

  protected onSsoFailed(message: string): void {
    this.snackBar.open(message, 'Dismiss', { duration: 6000, panelClass: ['snack-error'] });
  }
}
