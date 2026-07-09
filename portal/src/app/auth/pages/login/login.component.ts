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
import { AuthBrandHeaderComponent } from '../../components/auth-brand-header/auth-brand-header.component';
import { AppFooterComponent } from '../../../layout/app-footer/app-footer.component';
import { BrandingService } from '../../../services/branding.service';

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
    AuthBrandHeaderComponent,
    AppFooterComponent,
  ],
  templateUrl: './login.component.html',
  styleUrl: './login.component.scss',
})
export class LoginComponent {
  private readonly fb       = inject(FormBuilder);
  private readonly route    = inject(ActivatedRoute);
  protected readonly branding = inject(BrandingService);
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
    this.auth.login({ email: email.trim(), password, rememberMe }).subscribe();
  }

  // Helpers for template validation display
  protected fieldError(field: string, error: string): boolean {
    const ctrl = this.form.get(field);
    return !!(ctrl?.hasError(error) && (ctrl.touched || this.submitted()));
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
