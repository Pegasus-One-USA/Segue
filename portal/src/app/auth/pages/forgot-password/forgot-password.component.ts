// auth/pages/forgot-password/forgot-password.component.ts
import { Component, inject, signal } from '@angular/core';
import { ReactiveFormsModule, FormBuilder, Validators } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatIconModule } from '@angular/material/icon';
import { AuthService } from '../../services/auth.service';
import { AuthBrandHeaderComponent } from '../../components/auth-brand-header/auth-brand-header.component';

@Component({
  selector: 'app-forgot-password',
  standalone: true,
  imports: [
    ReactiveFormsModule,
    RouterLink,
    MatFormFieldModule,
    MatInputModule,
    MatButtonModule,
    MatProgressSpinnerModule,
    MatIconModule,
    AuthBrandHeaderComponent,
  ],
  templateUrl: './forgot-password.component.html',
  styleUrl: './forgot-password.component.scss',
})
export class ForgotPasswordComponent {
  private readonly fb     = inject(FormBuilder);
  private readonly auth   = inject(AuthService);

  protected readonly sent      = signal(false);
  protected readonly loading   = signal(false);
  protected readonly error     = signal<string | null>(null);
  protected readonly submitted = signal(false);

  /** The generic, enumeration-safe message from AuthApiService.forgotPassword() — "If an account exists
   *  for X, a password reset link has been sent." Shown verbatim rather than re-worded in the template so
   *  there's a single place this exact phrasing lives, not two copies that can drift apart. */
  protected successMessage = '';

  protected readonly form = this.fb.nonNullable.group({
    email: ['', [Validators.required, Validators.email]],
  });

  protected fieldError(field: string, errorKey: string): boolean {
    const ctrl = this.form.get(field);
    return !!(ctrl?.hasError(errorKey) && (ctrl.touched || this.submitted()));
  }

  protected submit(): void {
    this.submitted.set(true);
    this.form.markAllAsTouched();
    if (this.form.invalid) return;

    const email = this.form.getRawValue().email.trim();
    this.loading.set(true);
    this.error.set(null);

    this.auth.forgotPassword({ email }).subscribe({
      next: (res) => {
        this.loading.set(false);
        this.successMessage = res.message;
        this.sent.set(true);
      },
      error: (e) => {
        this.loading.set(false);
        // The backend only ever returns 202 here — a nonexistent email is NOT an error (see
        // LocalAuthService.ForgotPasswordAsync). What reaches here is a rate limit (429) or a genuine
        // network/server failure, so a generic message is correct; there's no "wrong email" case to
        // special-case without reintroducing an enumeration signal.
        this.error.set(
          e?.status === 0
            ? 'Network error. Please check your connection and try again.'
            : (e?.error?.message ?? 'Request failed. Please try again.')
        );
      },
    });
  }
}
